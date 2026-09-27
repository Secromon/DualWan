using Microsoft.Data.Sqlite;

namespace NetBinder.Service.Services;

/// <summary>A chart bucket containing traffic, latency, probe counts and WAN state.</summary>
public sealed record HistoryPoint(long Timestamp, double RxAverage, double RxMaximum, double TxAverage,
    double TxMaximum, double? LatencyMinimum, double? LatencyAverage, double? LatencyMaximum,
    long ProbeSuccesses, long ProbeFailures, string State);
/// <summary>Optional age and database-size limits; null disables the corresponding limit.</summary>
public sealed record StoragePolicy(int? RetentionDays = 30, long? MaximumBytes = 1_073_741_824);

/// <summary>
/// Owns the Service's SQLite history database. Collection and maintenance are
/// best-effort: a storage failure must not interrupt packet routing.
/// </summary>
public sealed class TelemetryHistoryStore : IAsyncDisposable
{
    private readonly string _path;
    private readonly IReadOnlyDictionary<string, NetBinder.Shared.Models.BindingMapping> _wans;
    private readonly WanHealthService _health;
    private readonly RedirectorService _redirector;
    private readonly ApplicationTrafficPersistence? _appTraffic;
    private readonly CancellationTokenSource _cts = new();
    // Serializes writes, queries and maintenance on the shared history state.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (long Rx, long Tx, long Success, long Failure, string State)> _last = new(StringComparer.OrdinalIgnoreCase);
    private (long Failover, long Failback, long Strict) _lastRouting;
    private Task? _worker;
    private StoragePolicy _policy = new();

    public TelemetryHistoryStore(string path, IReadOnlyDictionary<string, NetBinder.Shared.Models.BindingMapping> wans,
        WanHealthService health, RedirectorService redirector, ApplicationTrafficAccumulator? traffic = null)
    { _path = path; _wans = wans; _health = health; _redirector = redirector;
      if (traffic is not null) _appTraffic = new ApplicationTrafficPersistence(path, traffic, _gate); }

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    public async Task StartAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await InitializeAsync();
        if (_appTraffic is not null) await _appTraffic.InitializeAsync();
        await AddEventAsync("SERVICE_START", null, null);
        await MaintainAsync();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task InitializeAsync()
    {
        await using var db = new SqliteConnection(ConnectionString); await db.OpenAsync();
        // WAL permits readers while the sampling worker writes; NORMAL sync keeps
        // historical data less costly than the routing path it observes.
        await Command(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " +
            "CREATE TABLE IF NOT EXISTS raw_samples(ts INTEGER NOT NULL,wan TEXT NOT NULL,rx_rate REAL NOT NULL,tx_rate REAL NOT NULL,rx_delta INTEGER NOT NULL,tx_delta INTEGER NOT NULL,latency REAL NULL,probe_ok INTEGER NOT NULL,probe_fail INTEGER NOT NULL,state TEXT NOT NULL);" +
            "CREATE INDEX IF NOT EXISTS ix_raw_wan_ts ON raw_samples(wan,ts);" +
            "CREATE TABLE IF NOT EXISTS aggregates(ts INTEGER NOT NULL,wan TEXT NOT NULL,resolution INTEGER NOT NULL,rx_avg REAL NOT NULL,rx_max REAL NOT NULL,tx_avg REAL NOT NULL,tx_max REAL NOT NULL,lat_min REAL NULL,lat_avg REAL NULL,lat_max REAL NULL,probe_ok INTEGER NOT NULL,probe_fail INTEGER NOT NULL,state TEXT NOT NULL,PRIMARY KEY(ts,wan,resolution));" +
            "CREATE INDEX IF NOT EXISTS ix_agg_wan_ts ON aggregates(wan,ts,resolution);" +
            "CREATE TABLE IF NOT EXISTS events(ts INTEGER NOT NULL,type TEXT NOT NULL,wan TEXT NULL,detail TEXT NULL);" +
            "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);");
        _policy = new StoragePolicy(await ReadNullableInt(db, "retentionDays", 30), await ReadNullableLong(db, "maximumBytes", 1_073_741_824));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Record raw samples every five seconds and run retention maintenance daily.
        // A failed sample is logged locally and leaves the routing engine running.
        using var sample = new PeriodicTimer(TimeSpan.FromSeconds(5));
        DateTime nextMaintenance = DateTime.UtcNow.AddDays(1);
        long nextAppFlush = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60 + 60;
        DateTime nextAppMaintenance = DateTime.UtcNow.AddMinutes(15);
        try
        {
            while (await sample.WaitForNextTickAsync(ct))
            {
                try { await WriteSampleAsync(); if (DateTime.UtcNow >= nextMaintenance) { await MaintainAsync(); nextMaintenance = DateTime.UtcNow.AddDays(1); } }
                catch (Exception ex) { Console.WriteLine($"TELEMETRY DATABASE ERROR: {ex.GetType().Name}: {ex.Message}; routing continues"); }
                if (_appTraffic is not null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (now >= nextAppFlush)
                    {
                        await _appTraffic.FlushAsync(DateTimeOffset.UtcNow,
                            maximumBytes: _policy.MaximumBytes, ct: ct);
                        nextAppFlush = now / 60 * 60 + 60;
                    }
                    if (DateTime.UtcNow >= nextAppMaintenance)
                    {
                        await _appTraffic.MaintainAsync(DateTimeOffset.UtcNow, _policy.MaximumBytes, ct);
                        nextAppMaintenance = DateTime.UtcNow.AddMinutes(15);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteSampleAsync()
    {
        // Store deltas from cumulative health counters, then turn routing counter
        // increases into discrete events so history reflects each occurrence.
        await _gate.WaitAsync();
        try
        {
            await using var db = new SqliteConnection(ConnectionString); await db.OpenAsync();
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var wan in _wans.Keys)
            {
                var s = _health.GetTelemetry(wan); if (s is null) continue;
                var previous = _last.GetValueOrDefault(wan);
                long rx = s.RxTotalBytes ?? 0, tx = s.TxTotalBytes ?? 0;
                long ok = s.ProbeSuccesses, fail = s.ProbeFailures;
                string state = s.State.ToString().ToUpperInvariant();
                await using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO raw_samples VALUES($ts,$wan,$rxr,$txr,$rxd,$txd,$lat,$ok,$fail,$state)";
                cmd.Parameters.AddWithValue("$ts", ts); cmd.Parameters.AddWithValue("$wan", wan);
                cmd.Parameters.AddWithValue("$rxr", s.RxBytesPerSecond ?? 0); cmd.Parameters.AddWithValue("$txr", s.TxBytesPerSecond ?? 0);
                cmd.Parameters.AddWithValue("$rxd", Math.Max(0, rx - previous.Rx)); cmd.Parameters.AddWithValue("$txd", Math.Max(0, tx - previous.Tx));
                cmd.Parameters.AddWithValue("$lat", (object?)s.LatencyCurrentMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ok", Math.Max(0, ok - previous.Success)); cmd.Parameters.AddWithValue("$fail", Math.Max(0, fail - previous.Failure)); cmd.Parameters.AddWithValue("$state", state);
                await cmd.ExecuteNonQueryAsync();
                if (previous.State is not null && previous.State != state) await InsertEvent(db, ts, $"WAN_{state}", wan, $"{previous.State}->{state}");
                _last[wan] = (rx, tx, ok, fail, state);
            }
            var counters=(_redirector.FailoverCount,_redirector.FailbackCount,_redirector.StrictFailureCount);
            for(long i=_lastRouting.Failover;i<counters.Item1;i++)await InsertEvent(db,ts,"FAILOVER",null,null);
            for(long i=_lastRouting.Failback;i<counters.Item2;i++)await InsertEvent(db,ts,"FAILBACK",null,null);
            for(long i=_lastRouting.Strict;i<counters.Item3;i++)await InsertEvent(db,ts,"STRICT_FAILURE",null,null);
            _lastRouting=counters;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<HistoryPoint>> QueryAsync(string wan, DateTimeOffset from, DateTimeOffset to, string resolution)
    {
        // Query raw and previously compacted buckets together; time resolution is
        // selected for the requested range and the response is bounded for the UI.
        int bucket = resolution.ToLowerInvariant() switch { "5s" => 5, "1m" => 60, "5m" => 300, "15m" => 900, "auto" => (to-from).TotalHours <= 24 ? 60 : (to-from).TotalDays <= 7 ? 300 : 900, _ => throw new InvalidDataException("Invalid resolution.") };
        await _gate.WaitAsync();
        try
        {
            await using var db = new SqliteConnection(ConnectionString); await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT * FROM (SELECT (ts/$b)*$b t,AVG(rx_rate),MAX(rx_rate),AVG(tx_rate),MAX(tx_rate),MIN(latency),AVG(latency),MAX(latency),SUM(probe_ok),SUM(probe_fail),MAX(state) FROM raw_samples WHERE wan=$wan AND ts BETWEEN $from AND $to GROUP BY (ts/$b) UNION ALL SELECT ts,rx_avg,rx_max,tx_avg,tx_max,lat_min,lat_avg,lat_max,probe_ok,probe_fail,state FROM aggregates WHERE wan=$wan AND resolution=$b AND ts BETWEEN $from AND $to) ORDER BY 1 LIMIT 1500";
            cmd.Parameters.AddWithValue("$b", bucket); cmd.Parameters.AddWithValue("$wan", wan); cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            var result = new List<HistoryPoint>(); await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.IsDBNull(5)?null:reader.GetDouble(5), reader.IsDBNull(6)?null:reader.GetDouble(6), reader.IsDBNull(7)?null:reader.GetDouble(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10)));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<object> StatusAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = new SqliteConnection(ConnectionString); await db.OpenAsync();
            long raw=await Scalar(db,"SELECT COUNT(*) FROM raw_samples"), agg=await Scalar(db,"SELECT COUNT(*) FROM aggregates");
            long? oldest=await NullableScalar(db,"SELECT MIN(ts) FROM (SELECT ts FROM raw_samples UNION ALL SELECT ts FROM aggregates)");
            long? newest=await NullableScalar(db,"SELECT MAX(ts) FROM (SELECT ts FROM raw_samples UNION ALL SELECT ts FROM aggregates)");
            long size = File.Exists(_path) ? new FileInfo(_path).Length : 0; if(File.Exists(_path+"-wal")) size += new FileInfo(_path+"-wal").Length;
            return new { databasePath=_path,currentSizeBytes=size,retentionDays=_policy.RetentionDays,maximumSizeBytes=_policy.MaximumBytes,oldestRecord=oldest,newestRecord=newest,rawSampleCount=raw,aggregatedSampleCount=agg };
        }
        finally { _gate.Release(); }
    }

    public async Task SetPolicyAsync(StoragePolicy policy) { if(policy.RetentionDays is <1) throw new InvalidDataException("Retention must be at least one day."); if(policy.MaximumBytes is <1048576) throw new InvalidDataException("Maximum size must be at least 1 MB."); _policy=policy; await _gate.WaitAsync(); try { await using var db=new SqliteConnection(ConnectionString);await db.OpenAsync();await Set(db,"retentionDays",policy.RetentionDays?.ToString()??"unlimited");await Set(db,"maximumBytes",policy.MaximumBytes?.ToString()??"unlimited"); } finally{_gate.Release();} }

    public async Task MaintainAsync()
    {
        // Keep recent 5-second samples; compact older data to 1-, 5-, then
        // 15-minute buckets. Age retention applies to samples, buckets and events.
        // Size pruning prefers oldest aggregates and avoids recent raw samples.
        if (_appTraffic is not null)
            await _appTraffic.MaintainAsync(DateTimeOffset.UtcNow, _policy.MaximumBytes);
        await _gate.WaitAsync();
        try
        {
            await using var db=new SqliteConnection(ConnectionString);await db.OpenAsync();long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await Aggregate(db,60,now-86400);await Reaggregate(db,60,300,now-7*86400);await Reaggregate(db,300,900,now-30L*86400);
            if(_policy.RetentionDays.HasValue){long cutoff=now-_policy.RetentionDays.Value*86400L;await Exec(db,"DELETE FROM raw_samples WHERE ts<$x",cutoff);await Exec(db,"DELETE FROM aggregates WHERE ts<$x",cutoff);await Exec(db,"DELETE FROM events WHERE ts<$x",cutoff);}
            if(_policy.MaximumBytes.HasValue){for(int i=0;i<20 && DatabaseSize()>_policy.MaximumBytes.Value;i++){long changed=await Exec(db,"DELETE FROM aggregates WHERE rowid IN (SELECT rowid FROM aggregates ORDER BY ts LIMIT 1000)");if(changed==0)changed=await Exec(db,"DELETE FROM raw_samples WHERE rowid IN (SELECT rowid FROM raw_samples WHERE ts<$x ORDER BY ts LIMIT 1000)",now-3600);if(changed==0)break;}}
            await Command(db,"PRAGMA wal_checkpoint(PASSIVE);");
        }
        finally{_gate.Release();}
    }

    public Task<AppTrafficSummary> QueryAppSummaryAsync(string period, int limit) =>
        _appTraffic?.QuerySummaryAsync(period, limit) ??
        Task.FromException<AppTrafficSummary>(new InvalidOperationException("Application statistics unavailable."));

    public Task<AppTrafficDetail> QueryAppDetailAsync(string appKey, string period) =>
        _appTraffic?.QueryDetailAsync(appKey, period) ??
        Task.FromException<AppTrafficDetail>(new InvalidOperationException("Application statistics unavailable."));

    public int? AppStatsRetentionMinutes => _appTraffic?.Enabled == true ? _appTraffic.RetentionMinutes : null;

    public Task SetAppStatsRetentionAsync(int minutes) =>
        _appTraffic?.Enabled == true
            ? _appTraffic.SetRetentionAsync(minutes)
            : Task.FromException(new InvalidOperationException("Application statistics unavailable."));

    /// <summary>Call after relay producers have stopped; bounded best-effort final flush.</summary>
    public async Task FlushFinalAppTrafficAsync()
    {
        if (_appTraffic is null || !_appTraffic.Enabled) return;
        if (!await StopWorkerAsync())
        {
            Console.WriteLine("APP STATS SHUTDOWN FLUSH SKIPPED: history worker still active");
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        do
        {
            await _appTraffic.FlushAsync(DateTimeOffset.UtcNow, sealCurrent: true,
                maximumBytes: _policy.MaximumBytes, ct: timeout.Token);
            if (_appTraffic.UncollectedSealedBuckets == 0) break;
            try { await Task.Delay(50, timeout.Token); }
            catch (OperationCanceledException) { break; }
        } while (!timeout.IsCancellationRequested);
        if (_appTraffic.PendingBatches > 0 || _appTraffic.UncollectedSealedBuckets > 0)
            Console.WriteLine("APP STATS SHUTDOWN FLUSH INCOMPLETE; routing shutdown continues");
    }

    // Insert the aggregate and remove its source rows in one transaction.
    private async Task Aggregate(SqliteConnection db,int resolution,long cutoff){await using var tx=await db.BeginTransactionAsync();await using var cmd=db.CreateCommand();cmd.Transaction=(SqliteTransaction)tx;cmd.CommandText="INSERT OR REPLACE INTO aggregates SELECT (ts/$r)*$r,wan,$r,AVG(rx_rate),MAX(rx_rate),AVG(tx_rate),MAX(tx_rate),MIN(latency),AVG(latency),MAX(latency),SUM(probe_ok),SUM(probe_fail),MAX(state) FROM raw_samples WHERE ts<$c GROUP BY wan,(ts/$r)";cmd.Parameters.AddWithValue("$r",resolution);cmd.Parameters.AddWithValue("$c",cutoff);await cmd.ExecuteNonQueryAsync();cmd.CommandText="DELETE FROM raw_samples WHERE ts<$c";await cmd.ExecuteNonQueryAsync();await tx.CommitAsync();}
    private static async Task Reaggregate(SqliteConnection db,int source,int target,long cutoff){await using var tx=await db.BeginTransactionAsync();await using var cmd=db.CreateCommand();cmd.Transaction=(SqliteTransaction)tx;cmd.CommandText="INSERT OR REPLACE INTO aggregates SELECT (ts/$t)*$t,wan,$t,AVG(rx_avg),MAX(rx_max),AVG(tx_avg),MAX(tx_max),MIN(lat_min),AVG(lat_avg),MAX(lat_max),SUM(probe_ok),SUM(probe_fail),MAX(state) FROM aggregates WHERE resolution=$s AND ts<$c GROUP BY wan,(ts/$t)";cmd.Parameters.AddWithValue("$s",source);cmd.Parameters.AddWithValue("$t",target);cmd.Parameters.AddWithValue("$c",cutoff);await cmd.ExecuteNonQueryAsync();cmd.CommandText="DELETE FROM aggregates WHERE resolution=$s AND ts<$c";await cmd.ExecuteNonQueryAsync();await tx.CommitAsync();}
    private long DatabaseSize()=> (File.Exists(_path)?new FileInfo(_path).Length:0)+(File.Exists(_path+"-wal")?new FileInfo(_path+"-wal").Length:0);
    private static async Task Command(SqliteConnection db,string sql){await using var c=db.CreateCommand();c.CommandText=sql;await c.ExecuteNonQueryAsync();}
    private static async Task<long> Exec(SqliteConnection db,string sql,long? x=null){await using var c=db.CreateCommand();c.CommandText=sql;if(x.HasValue)c.Parameters.AddWithValue("$x",x.Value);return await c.ExecuteNonQueryAsync();}
    private static async Task<long> Scalar(SqliteConnection db,string sql){await using var c=db.CreateCommand();c.CommandText=sql;return Convert.ToInt64(await c.ExecuteScalarAsync());}
    private static async Task<long?> NullableScalar(SqliteConnection db,string sql){await using var c=db.CreateCommand();c.CommandText=sql;var v=await c.ExecuteScalarAsync();return v is null or DBNull?null:Convert.ToInt64(v);}
    private static async Task Set(SqliteConnection db,string key,string value){await using var c=db.CreateCommand();c.CommandText="INSERT OR REPLACE INTO settings VALUES($k,$v)";c.Parameters.AddWithValue("$k",key);c.Parameters.AddWithValue("$v",value);await c.ExecuteNonQueryAsync();}
    private static async Task<int?> ReadNullableInt(SqliteConnection db,string key,int fallback){string? v=await Read(db,key);return v is null?fallback:v=="unlimited"?null:int.Parse(v);}
    private static async Task<long?> ReadNullableLong(SqliteConnection db,string key,long fallback){string? v=await Read(db,key);return v is null?fallback:v=="unlimited"?null:long.Parse(v);}
    private static async Task<string?> Read(SqliteConnection db,string key){await using var c=db.CreateCommand();c.CommandText="SELECT value FROM settings WHERE key=$k";c.Parameters.AddWithValue("$k",key);return await c.ExecuteScalarAsync() as string;}
    private static async Task InsertEvent(SqliteConnection db,long ts,string type,string? wan,string? detail){await using var c=db.CreateCommand();c.CommandText="INSERT INTO events VALUES($t,$type,$wan,$detail)";c.Parameters.AddWithValue("$t",ts);c.Parameters.AddWithValue("$type",type);c.Parameters.AddWithValue("$wan",(object?)wan??DBNull.Value);c.Parameters.AddWithValue("$detail",(object?)detail??DBNull.Value);await c.ExecuteNonQueryAsync();}
    // Event persistence is deliberately isolated from its Service caller.
    public async Task AddEventAsync(string type,string? wan,string? detail){try{await _gate.WaitAsync();try{await using var db=new SqliteConnection(ConnectionString);await db.OpenAsync();await InsertEvent(db,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),type,wan,detail);}finally{_gate.Release();}}catch(Exception ex){Console.WriteLine($"TELEMETRY EVENT ERROR: {ex.Message}");}}
    private async Task<bool> StopWorkerAsync()
    {
        _cts.Cancel();
        if (_worker is null) return true;
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)); return true; }
        catch (TimeoutException) { Console.WriteLine("TELEMETRY WORKER STOP TIMEOUT; shutdown continues"); return false; }
        catch (Exception ex) { Console.WriteLine($"TELEMETRY WORKER STOP ERROR: {ex.GetType().Name}"); return true; }
    }

    public async ValueTask DisposeAsync()
    {
        if (!await StopWorkerAsync()) return;
        await AddEventAsync("SERVICE_STOP", null, null);
        _cts.Dispose();
        _gate.Dispose();
    }
}
