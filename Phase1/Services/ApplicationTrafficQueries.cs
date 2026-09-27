using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace NetBinder.Service.Services;

public sealed record AppTrafficRow(
    [property: JsonPropertyName("appKey")] string AppKey,
    [property: JsonPropertyName("displayHint")] string DisplayHint,
    [property: JsonPropertyName("uploadBytes")] long UploadBytes,
    [property: JsonPropertyName("downloadBytes")] long DownloadBytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("tcpFlows")] long TcpFlows,
    [property: JsonPropertyName("udpSessions")] long UdpSessions);

public sealed record AppWanTraffic(
    [property: JsonPropertyName("wan")] string Wan,
    [property: JsonPropertyName("uploadBytes")] long UploadBytes,
    [property: JsonPropertyName("downloadBytes")] long DownloadBytes,
    [property: JsonPropertyName("tcpFlows")] long TcpFlows,
    [property: JsonPropertyName("udpSessions")] long UdpSessions);

public sealed record AppTrafficPoint(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("uploadBytes")] long UploadBytes,
    [property: JsonPropertyName("downloadBytes")] long DownloadBytes,
    [property: JsonPropertyName("tcpFlows")] long TcpFlows,
    [property: JsonPropertyName("udpSessions")] long UdpSessions);

public sealed record AppTrafficSummary(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("retentionMinutes")] int RetentionMinutes,
    [property: JsonPropertyName("fromTimestamp")] long FromTimestamp,
    [property: JsonPropertyName("toTimestamp")] long ToTimestamp,
    [property: JsonPropertyName("oldestAvailableTimestamp")] long? OldestAvailableTimestamp,
    [property: JsonPropertyName("coverage")] string Coverage,
    [property: JsonPropertyName("applications")] IReadOnlyList<AppTrafficRow> Applications);

public sealed record AppTrafficDetail(
    [property: JsonPropertyName("appKey")] string AppKey,
    [property: JsonPropertyName("displayHint")] string DisplayHint,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("retentionMinutes")] int RetentionMinutes,
    [property: JsonPropertyName("fromTimestamp")] long FromTimestamp,
    [property: JsonPropertyName("toTimestamp")] long ToTimestamp,
    [property: JsonPropertyName("oldestAvailableTimestamp")] long? OldestAvailableTimestamp,
    [property: JsonPropertyName("coverage")] string Coverage,
    [property: JsonPropertyName("uploadBytes")] long UploadBytes,
    [property: JsonPropertyName("downloadBytes")] long DownloadBytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("tcpFlows")] long TcpFlows,
    [property: JsonPropertyName("udpSessions")] long UdpSessions,
    [property: JsonPropertyName("wanBreakdown")] IReadOnlyList<AppWanTraffic> WanBreakdown,
    [property: JsonPropertyName("series")] IReadOnlyList<AppTrafficPoint> Series);

public sealed partial class ApplicationTrafficPersistence
{
    public const string Coverage = "dualwan-routed-ipv4";
    private readonly record struct QueryWindow(long RequestedFrom, long From, long To,
        int ResolutionSeconds, int MaximumPoints);

    public static bool IsSupportedPeriod(string? period) => period is "1h" or "24h" or "7d" or "30d";

    private QueryWindow Window(string period, DateTimeOffset now)
    {
        (int duration, int resolution, int maximum) = period switch
        {
            "1h" => (3600, 60, 60),
            "24h" => (86400, 900, 96),
            "7d" => (604800, 3600, 168),
            "30d" => (2592000, 86400, 30),
            _ => throw new ArgumentException("Invalid statistics period.", nameof(period))
        };
        long to = now.ToUniversalTime().ToUnixTimeSeconds() / 60 * 60;
        long requested = to - duration;
        // Retention and requested period are both bounded. The last partial
        // minute is deliberately excluded; no mutable RAM snapshot is merged.
        long from = Math.Max(requested, to - RetentionMinutes * 60L);
        return new(requested, from, to, resolution, maximum);
    }

    public async Task<AppTrafficSummary> QuerySummaryAsync(string period, int limit = 20,
        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (!Enabled) throw new InvalidOperationException("Application statistics unavailable.");
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        QueryWindow window = Window(period, now ?? DateTimeOffset.UtcNow);
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new TimeoutException("SQLite gate busy");
        try
        {
            await using var db = new SqliteConnection(ConnectionString);
            await db.OpenAsync(ct);
            long? oldest = await Oldest(db, window, null, ct);
            await using var command = db.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = "SELECT app_key,SUM(upload_bytes),SUM(download_bytes)," +
                "SUM(tcp_flows),SUM(udp_sessions) FROM app_traffic_minute " +
                "WHERE bucket_ts >= $from AND bucket_ts < $to GROUP BY app_key " +
                "ORDER BY SUM(upload_bytes + download_bytes) DESC,app_key LIMIT $limit";
            AddWindow(command, window);
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<AppTrafficRow>(limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string key = reader.GetString(0);
                long upload = reader.GetInt64(1), download = reader.GetInt64(2);
                rows.Add(new(key, Path.GetFileName(key), upload, download,
                    checked(upload + download), reader.GetInt64(3), reader.GetInt64(4)));
            }
            return new(period, RetentionMinutes, window.From, window.To, oldest, Coverage, rows);
        }
        finally { _gate.Release(); }
    }

    public async Task<AppTrafficDetail> QueryDetailAsync(string appKey, string period,
        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (!Enabled) throw new InvalidOperationException("Application statistics unavailable.");
        if (string.IsNullOrWhiteSpace(appKey) || appKey.Length > 32767)
            throw new ArgumentException("Invalid application key.", nameof(appKey));
        QueryWindow window = Window(period, now ?? DateTimeOffset.UtcNow);
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct)) throw new TimeoutException("SQLite gate busy");
        try
        {
            await using var db = new SqliteConnection(ConnectionString);
            await db.OpenAsync(ct);
            long? oldest = await Oldest(db, window, appKey, ct);
            var wans = new List<AppWanTraffic>(2);
            await using (var command = db.CreateCommand())
            {
                command.CommandTimeout = 2;
                command.CommandText = "SELECT wan,SUM(upload_bytes),SUM(download_bytes)," +
                    "SUM(tcp_flows),SUM(udp_sessions) FROM app_traffic_minute " +
                    "WHERE app_key=$app AND bucket_ts >= $from AND bucket_ts < $to GROUP BY wan ORDER BY wan";
                AddWindow(command, window);
                command.Parameters.AddWithValue("$app", appKey);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    wans.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                        reader.GetInt64(3), reader.GetInt64(4)));
            }
            var series = new List<AppTrafficPoint>(window.MaximumPoints);
            await using (var command = db.CreateCommand())
            {
                command.CommandTimeout = 2;
                // Anchor bins at the requested window start, yielding at most
                // 60/96/168/30 points even for an unaligned wall clock.
                command.CommandText = "SELECT $start + ((bucket_ts-$start)/$res)*$res AS slot," +
                    "SUM(upload_bytes),SUM(download_bytes),SUM(tcp_flows),SUM(udp_sessions) " +
                    "FROM app_traffic_minute WHERE app_key=$app AND bucket_ts >= $from AND bucket_ts < $to " +
                    "GROUP BY slot ORDER BY slot LIMIT $max";
                AddWindow(command, window);
                command.Parameters.AddWithValue("$start", window.RequestedFrom);
                command.Parameters.AddWithValue("$res", window.ResolutionSeconds);
                command.Parameters.AddWithValue("$max", window.MaximumPoints);
                command.Parameters.AddWithValue("$app", appKey);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    series.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                        reader.GetInt64(3), reader.GetInt64(4)));
            }
            long upload = wans.Sum(x => x.UploadBytes), download = wans.Sum(x => x.DownloadBytes);
            return new(appKey, Path.GetFileName(appKey), period, RetentionMinutes,
                window.From, window.To, oldest, Coverage, upload, download,
                checked(upload + download), wans.Sum(x => x.TcpFlows), wans.Sum(x => x.UdpSessions),
                wans, series);
        }
        finally { _gate.Release(); }
    }

    private static void AddWindow(SqliteCommand command, QueryWindow window)
    {
        command.Parameters.AddWithValue("$from", window.From);
        command.Parameters.AddWithValue("$to", window.To);
    }

    private static async Task<long?> Oldest(SqliteConnection db, QueryWindow window, string? appKey,
        CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = "SELECT MIN(bucket_ts) FROM app_traffic_minute WHERE bucket_ts >= $from " +
            "AND bucket_ts < $to" + (appKey is null ? "" : " AND app_key=$app");
        AddWindow(command, window);
        if (appKey is not null) command.Parameters.AddWithValue("$app", appKey);
        object? value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }
}
