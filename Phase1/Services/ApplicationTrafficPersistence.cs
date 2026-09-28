using Microsoft.Data.Sqlite;

namespace NetBinder.Service.Services;

/// <summary>Best-effort, bounded persistence of completed DualWAN relay minutes.</summary>
public sealed partial class ApplicationTrafficPersistence
{
    private const int MaxPendingMinutes = 4;
    private const int MaxDeleteBatches = 20;
    private const int DeleteBatchRows = 1000;
    private readonly string _path;
    private readonly ApplicationTrafficAccumulator _traffic;
    private readonly SemaphoreSlim _gate;
    private readonly Queue<IReadOnlyList<ApplicationTrafficAccumulator.Snapshot>> _pending = new();
    private long _droppedBatches;
    private DateTimeOffset _lastFailureLog;
    private bool _failureActive;

    public bool Enabled { get; private set; }
    public int RetentionMinutes { get; private set; } = 60;
    public long DroppedBatches => Interlocked.Read(ref _droppedBatches);
    public int PendingBatches => _pending.Count;
    public int UncollectedSealedBuckets => _traffic.PendingSealedBuckets;

    public ApplicationTrafficPersistence(string path, ApplicationTrafficAccumulator traffic,
        SemaphoreSlim? sharedGate = null)
    { _path = path; _traffic = traffic; _gate = sharedGate ?? new SemaphoreSlim(1, 1); }

    private string ConnectionString => new SqliteConnectionStringBuilder
    { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 2 }.ToString();

    public static bool IsSupportedRetention(int minutes) => minutes is 60 or 1440 or 10080 or 43200;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new TimeoutException("SQLite gate busy");
            try
            {
                await using var db = new SqliteConnection(ConnectionString);
                await db.OpenAsync(ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await Execute(db, (SqliteTransaction)tx,
                    "CREATE TABLE IF NOT EXISTS app_traffic_minute(" +
                    "bucket_ts INTEGER NOT NULL,app_key TEXT NOT NULL,wan TEXT NOT NULL," +
                    "upload_bytes INTEGER NOT NULL,download_bytes INTEGER NOT NULL," +
                    "tcp_flows INTEGER NOT NULL,udp_sessions INTEGER NOT NULL," +
                    "PRIMARY KEY(bucket_ts,app_key,wan));", ct);
                await Execute(db, (SqliteTransaction)tx,
                    "CREATE INDEX IF NOT EXISTS ix_app_traffic_app_ts ON app_traffic_minute(app_key,bucket_ts);", ct);
                await Execute(db, (SqliteTransaction)tx,
                    "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);", ct);
                await tx.CommitAsync(ct);
                await using var read = db.CreateCommand();
                read.CommandText = "SELECT value FROM settings WHERE key='appStatsRetentionMinutes'";
                string? raw = await read.ExecuteScalarAsync(ct) as string;
                if (raw is not null && (!int.TryParse(raw, out int value) || !IsSupportedRetention(value)))
                    Console.WriteLine("APP STATS RETENTION INVALID: using 60 minutes; routing continues");
                else if (raw is not null) RetentionMinutes = int.Parse(raw);
                Enabled = true;
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            Enabled = false;
            Console.WriteLine($"APP STATS SCHEMA ERROR: {ex.GetType().Name}; persistence disabled; routing continues");
        }
    }

    public async Task SetRetentionAsync(int minutes, CancellationToken ct = default)
    {
        if (!IsSupportedRetention(minutes))
        {
            minutes = 60;
            RetentionMinutes = 60;
            Console.WriteLine("APP STATS RETENTION INVALID: using 60 minutes; routing continues");
        }
        if (!Enabled) return;
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new TimeoutException("SQLite gate busy");
        try
        {
            await using var db = new SqliteConnection(ConnectionString);
            await db.OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO settings(key,value) VALUES('appStatsRetentionMinutes',$v) " +
                                  "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$v", minutes.ToString());
            await command.ExecuteNonQueryAsync(ct);
            RetentionMinutes = minutes;
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync(DateTimeOffset now, bool sealCurrent = false,
        long? maximumBytes = null, CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            foreach (var batch in _traffic.CollectSealed(now, sealCurrent))
            {
                if (_pending.Count == MaxPendingMinutes)
                {
                    _pending.Dequeue(); // Prefer recent data over the oldest failed minute.
                    Interlocked.Increment(ref _droppedBatches);
                    LogFailure("APP STATS BACKLOG FULL: oldest minute dropped");
                }
                _pending.Enqueue(batch);
            }
            if (_pending.Count == 0) return;
            if (maximumBytes.HasValue && DatabaseSize() >= maximumBytes.Value)
            {
                Interlocked.Add(ref _droppedBatches, _pending.Count);
                _pending.Clear();
                LogFailure("APP STATS QUOTA REACHED: pending minutes dropped; routing continues");
                return;
            }
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new TimeoutException("SQLite gate busy");
            try
            {
                await using var db = new SqliteConnection(ConnectionString);
                await db.OpenAsync(ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using var command = db.CreateCommand();
                command.Transaction = (SqliteTransaction)tx;
                command.CommandText = "INSERT INTO app_traffic_minute VALUES($ts,$app,$wan,$up,$down,$tcp,$udp) " +
                    "ON CONFLICT(bucket_ts,app_key,wan) DO UPDATE SET " +
                    "upload_bytes=excluded.upload_bytes,download_bytes=excluded.download_bytes," +
                    "tcp_flows=excluded.tcp_flows,udp_sessions=excluded.udp_sessions";
                var ts = command.Parameters.Add("$ts", SqliteType.Integer);
                var app = command.Parameters.Add("$app", SqliteType.Text);
                var wan = command.Parameters.Add("$wan", SqliteType.Text);
                var up = command.Parameters.Add("$up", SqliteType.Integer);
                var down = command.Parameters.Add("$down", SqliteType.Integer);
                var tcp = command.Parameters.Add("$tcp", SqliteType.Integer);
                var udp = command.Parameters.Add("$udp", SqliteType.Integer);
                foreach (var batch in _pending)
                    foreach (var row in batch)
                    {
                        ts.Value = row.MinuteUtc; app.Value = row.ApplicationPath; wan.Value = row.Wan;
                        up.Value = row.UploadBytes; down.Value = row.DownloadBytes;
                        tcp.Value = row.TcpFlows; udp.Value = row.UdpSessions;
                        await command.ExecuteNonQueryAsync(ct);
                    }
                await tx.CommitAsync(ct);
                _pending.Clear();
                if (_failureActive) Console.WriteLine("APP STATS PERSISTENCE RECOVERED");
                _failureActive = false;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure($"APP STATS PERSISTENCE ERROR: {ex.GetType().Name}; routing continues");
        }
    }

    public async Task MaintainAsync(DateTimeOffset now, long? maximumBytes, CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new TimeoutException("SQLite gate busy");
            try
            {
                await using var db = new SqliteConnection(ConnectionString);
                await db.OpenAsync(ct);
                long cutoff = now.ToUnixTimeSeconds() - RetentionMinutes * 60L;
                for (int i = 0; i < MaxDeleteBatches; i++)
                    if (await DeleteBatch(db, "WHERE bucket_ts < $cutoff", cutoff, ct) < DeleteBatchRows) break;
                if (maximumBytes.HasValue)
                    for (int i = 0; i < MaxDeleteBatches && DatabaseSize() > maximumBytes.Value; i++)
                        if (await DeleteBatch(db, "", null, ct) == 0) break;
                await using var checkpoint = db.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
                await checkpoint.ExecuteNonQueryAsync(ct);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            LogFailure($"APP STATS MAINTENANCE ERROR: {ex.GetType().Name}; routing continues");
        }
    }

    private static async Task<int> DeleteBatch(SqliteConnection db, string condition, long? cutoff,
        CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM app_traffic_minute WHERE rowid IN " +
            $"(SELECT rowid FROM app_traffic_minute {condition} ORDER BY bucket_ts LIMIT {DeleteBatchRows})";
        if (cutoff.HasValue) command.Parameters.AddWithValue("$cutoff", cutoff.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task Execute(SqliteConnection db, SqliteTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = tx; command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private long DatabaseSize() =>
        (File.Exists(_path) ? new FileInfo(_path).Length : 0) +
        (File.Exists(_path + "-wal") ? new FileInfo(_path + "-wal").Length : 0);

    private void LogFailure(string message)
    {
        _failureActive = true;
        if (DateTimeOffset.UtcNow - _lastFailureLog < TimeSpan.FromMinutes(5)) return;
        _lastFailureLog = DateTimeOffset.UtcNow;
        Console.WriteLine(message);
    }
}
