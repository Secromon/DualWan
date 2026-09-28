using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetBinder.Service.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task AddRows(string path, IEnumerable<(long Timestamp, string App, string Wan,
    long Up, long Down, long Tcp, long Udp)> rows)
{
    await using var db = new SqliteConnection($"Data Source={path}");
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    await using var command = db.CreateCommand();
    command.Transaction = (SqliteTransaction)tx;
    command.CommandText = "INSERT OR REPLACE INTO app_traffic_minute VALUES($ts,$app,$wan,$up,$down,$tcp,$udp)";
    var ts = command.Parameters.Add("$ts", SqliteType.Integer);
    var app = command.Parameters.Add("$app", SqliteType.Text);
    var wan = command.Parameters.Add("$wan", SqliteType.Text);
    var up = command.Parameters.Add("$up", SqliteType.Integer);
    var down = command.Parameters.Add("$down", SqliteType.Integer);
    var tcp = command.Parameters.Add("$tcp", SqliteType.Integer);
    var udp = command.Parameters.Add("$udp", SqliteType.Integer);
    foreach (var row in rows)
    {
        ts.Value = row.Timestamp; app.Value = row.App; wan.Value = row.Wan;
        up.Value = row.Up; down.Value = row.Down; tcp.Value = row.Tcp; udp.Value = row.Udp;
        await command.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
}

static async Task Exec(string path, string sql)
{
    await using var db = new SqliteConnection($"Data Source={path}");
    await db.OpenAsync();
    await using var command = db.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

string folder = Path.Combine(Path.GetTempPath(), "DualWAN-QueryTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string path = Path.Combine(folder, "telemetry.db");
    // The IPC dispatcher uses the current clock; keep synthetic buckets in its window.
    DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60);
    long second = now.ToUnixTimeSeconds();
    string appA = @"C:\APP\A.EXE", appB = @"C:\APP\B.EXE", appC = @"C:\APP\C.EXE";
    var store = new ApplicationTrafficPersistence(path, new ApplicationTrafficAccumulator());
    await store.InitializeAsync();
    var empty = await store.QuerySummaryAsync("1h", now: now);
    Check(empty.Applications.Count == 0 && empty.OldestAvailableTimestamp is null &&
          empty.Coverage == ApplicationTrafficPersistence.Coverage && empty.RetentionMinutes == 60,
          "Q1/Q18 empty/default/coverage failed");
    Check(empty.FromTimestamp == second - 3600 && empty.ToTimestamp == second,
        "Q6 empty range metadata failed");
    await store.SetRetentionAsync(43200);
    await AddRows(path, new[]
    {
        (second-50*60, appA, "WAN1", 100L, 10L, 1L, 0L),
        (second-49*60, appA, "WAN1", 20L, 5L, 0L, 1L),
        (second-45*60, appA, "WAN2", 200L, 50L, 1L, 0L),
        (second-90*60, appA, "WAN1", 10L, 1L, 1L, 0L),
        (second-2*86400L, appA, "WAN2", 30L, 3L, 1L, 0L),
        (second-20*86400L, appA, "WAN1", 40L, 4L, 1L, 0L),
        (second-31*86400L, appA, "WAN1", 999L, 0L, 0L, 0L),
        (second-20*60, appB, "WAN1", 1000L, 100L, 1L, 0L),
        (second-10*60, appC, "WAN1", 5L, 1L, 1L, 0L)
    });
    var summary = await store.QuerySummaryAsync("1h", now: now);
    Check(summary.Applications.Count == 3 && summary.Applications[0].AppKey == appB &&
          summary.Applications[1].AppKey == appA && summary.Applications[1].TotalBytes == 385,
          "Q2/Q3 top ordering or sum failed");
    Check((await store.QuerySummaryAsync("1h", 1, now)).Applications.Count == 1 &&
          (await store.QuerySummaryAsync("1h", 20, now)).Applications.Count == 3 &&
          (await store.QuerySummaryAsync("1h", 100, now)).Applications.Count == 3,
          "Q4 limits failed");
    try { await store.QuerySummaryAsync("1h", 0, now); throw new Exception("Q5 invalid limit accepted"); }
    catch (ArgumentOutOfRangeException) { }
    try { await store.QuerySummaryAsync("bad", now: now); throw new Exception("Q10 invalid period accepted"); }
    catch (ArgumentException) { }

    long[] expectedUpload = [320, 330, 360, 400];
    long[] expectedDownload = [65, 66, 69, 73];
    string[] periods = ["1h", "24h", "7d", "30d"];
    int[] maximumPoints = [60, 96, 168, 30];
    for (int i = 0; i < periods.Length; i++)
    {
        var detail = await store.QueryDetailAsync(appA, periods[i], now);
        Check(detail.UploadBytes == expectedUpload[i] && detail.DownloadBytes == expectedDownload[i] &&
              detail.Series.Sum(x => x.UploadBytes) == detail.UploadBytes &&
              detail.Series.Sum(x => x.DownloadBytes) == detail.DownloadBytes &&
              detail.Series.Count <= maximumPoints[i],
              $"Q6-Q9/Q13-Q17 totals or resolution failed for {periods[i]}");
    }
    var oneHour = await store.QueryDetailAsync(appA, "1h", now);
    Check(oneHour.WanBreakdown.Count == 2 &&
          oneHour.WanBreakdown.Single(x => x.Wan == "WAN1").UploadBytes == 120 &&
          oneHour.WanBreakdown.Single(x => x.Wan == "WAN2").UploadBytes == 200 &&
          oneHour.Series.Count == 3,
          "Q11/Q12/Q13 WAN split or 1m series failed");
    Check((await store.QueryDetailAsync(appA, "24h", now)).Series.Count == 3 &&
          (await store.QueryDetailAsync(appA, "7d", now)).Series.Count == 3 &&
          (await store.QueryDetailAsync(appA, "30d", now)).Series.Count == 3,
          "Q14-Q16 grouping failed");
    var missing = await store.QueryDetailAsync(@"C:\ABSENT.EXE", "1h", now);
    Check(missing.TotalBytes == 0 && missing.Series.Count == 0 && missing.WanBreakdown.Count == 0,
        "missing app should return empty result");

    await store.SetRetentionAsync(60);
    var clipped = await store.QuerySummaryAsync("30d", now: now);
    Check(clipped.FromTimestamp == second-3600 && clipped.Applications.Single(x => x.AppKey == appA).UploadBytes == 320,
        "requested period longer than retention not clipped");
    foreach (int minutes in new[] { 1440, 10080, 43200 })
    {
        await store.SetRetentionAsync(minutes);
        Check(store.RetentionMinutes == minutes, "Q19-Q21 retention mutation failed");
        var reopened = new ApplicationTrafficPersistence(path, new ApplicationTrafficAccumulator());
        await reopened.InitializeAsync();
        Check(reopened.RetentionMinutes == minutes, "Q19-Q21 retention not persisted");
    }
    Check(!ApplicationTrafficPersistence.IsSupportedRetention(5) && store.RetentionMinutes == 43200,
        "Q22 invalid retention changed current value");

    // A larger synthetic table exercises result-size limits without WAN traffic.
    var many = new List<(long, string, string, long, long, long, long)>();
    for (int a = 0; a < 120; a++)
        for (int minute = 1; minute <= 150; minute++)
            many.Add((second-minute*60L, $@"C:\MANY\APP{a:D3}.EXE", "WAN1", 1, 0, 0, 0));
    await AddRows(path, many);
    var watch = Stopwatch.StartNew();
    var top = await store.QuerySummaryAsync("30d", 100, now);
    watch.Stop();
    var detailWatch = Stopwatch.StartNew();
    var largeDetail = await store.QueryDetailAsync(appA, "30d", now);
    detailWatch.Stop();
    Check(top.Applications.Count == 100 && largeDetail.Series.Count <= 30,
        "Q26 large DB response not bounded");
    Console.WriteLine($"Synthetic query timings: top-100/30d={watch.Elapsed.TotalMilliseconds:F2} ms; detail/30d={detailWatch.Elapsed.TotalMilliseconds:F2} ms; summary JSON={JsonSerializer.Serialize(top).Length} chars");

    // Parser/API version and admin gate tested without connecting to the installed pipe.
    var proxy = DispatchProxy.Create<IDualWanControlPlane, TestControlPlane>();
    ((TestControlPlane)(object)proxy).Store = store;
    await using var server = new ControlPipeServer(proxy, "DualWAN.QueryTests." + Guid.NewGuid().ToString("N"));
    using var pipe = new NamedPipeServerStream("DualWAN.QueryTests.Disconnected");
    MethodInfo process = typeof(ControlPipeServer).GetMethod("ProcessRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    async Task<JsonElement> Request(object request)
    {
        string line = JsonSerializer.Serialize(request);
        var task = (Task<object>)process.Invoke(server, [pipe, line, CancellationToken.None])!;
        return JsonSerializer.SerializeToElement(await task);
    }
    var ping = await Request(new { apiVersion = 1, requestId = "ping", command = "ping" });
    Check(ping.GetProperty("success").GetBoolean(), "Q27 existing ping command regressed");
    var version = await Request(new { apiVersion = 2, requestId = "ver", command = "ping" });
    Check(version.GetProperty("error").GetProperty("code").GetString() == "UNSUPPORTED_API_VERSION",
        "Q28 API version changed");
    var badPeriod = await Request(new { apiVersion = 1, requestId = "period", command = "getAppStatisticsSummary", period = "invalid" });
    Check(badPeriod.GetProperty("error").GetProperty("code").GetString() == "INVALID_PERIOD",
        "Q10 stable invalid-period IPC error failed");
    var badLimit = await Request(new { apiVersion = 1, requestId = "limit", command = "getAppStatisticsSummary", period = "1h", limit = 0 });
    Check(badLimit.GetProperty("error").GetProperty("code").GetString() == "INVALID_LIMIT",
        "Q5 stable invalid-limit IPC error failed");
    var readRetention = await Request(new { apiVersion = 1, requestId = "ret", command = "getAppStatsRetention" });
    Check(readRetention.GetProperty("data").GetProperty("retentionMinutes").GetInt32() == 43200 &&
          readRetention.GetProperty("data").GetProperty("allowedValues").GetArrayLength() == 4,
          "Q18 retention read IPC failed");
    var ipcSummary = await Request(new { apiVersion = 1, requestId = "summary", command = "getAppStatisticsSummary", period = "1h", limit = 2 });
    Check(ipcSummary.GetProperty("success").GetBoolean() &&
          ipcSummary.GetProperty("data").GetProperty("applications").GetArrayLength() == 2,
          "summary IPC response failed");
    var ipcDetail = await Request(new { apiVersion = 1, requestId = "detail", command = "getAppStatisticsDetail", appKey = appA, period = "1h" });
    Check(ipcDetail.GetProperty("success").GetBoolean() &&
          ipcDetail.GetProperty("data").GetProperty("appKey").GetString() == appA,
          "detail IPC response failed");
    await Exec(path, "DROP TABLE app_traffic_minute");
    try { await store.QuerySummaryAsync("1h", now: now); throw new Exception("Q24 missing table accepted"); }
    catch (SqliteException) { }
    try { await store.QueryDetailAsync(appA, "1h", now); throw new Exception("Q25 SQL failure accepted"); }
    catch (SqliteException) { }
    var unavailable = await Request(new { apiVersion = 1, requestId = "missing", command = "getAppStatisticsSummary", period = "1h" });
    Check(unavailable.GetProperty("error").GetProperty("code").GetString() == "APP_STATS_UNAVAILABLE",
          "Q24/Q25 SQLite exception was not converted to stable IPC error");
    var unauthorized = await Request(new { apiVersion = 1, requestId = "auth", command = "setAppStatsRetention", retentionMinutes = 1440 });
    Check(unauthorized.GetProperty("error").GetProperty("code").GetString() == "UNAUTHORIZED",
        "Q23 retention mutation bypassed admin gate");
    Assembly assembly = typeof(ApplicationTrafficPersistence).Assembly;
    Type pathsType = assembly.GetType("RuntimePaths", true)!;
    Type optionsType = assembly.GetType("RuntimeOptions", true)!;
    Type runtimeType = assembly.GetType("DualWanRuntime", true)!;
    object paths = Activator.CreateInstance(pathsType, folder, Path.Combine(folder, "config.json"),
        Path.Combine(folder, "logs"), Path.Combine(folder, "state.json"), path)!;
    object options = Activator.CreateInstance(optionsType, paths, false, false, "DualWAN.QueryTests")!;
    var runtime = (IDualWanControlPlane)Activator.CreateInstance(runtimeType, options)!;
    Check((await runtime.GetAppStatisticsSummaryAsync("bad", 20)).ErrorCode == "INVALID_PERIOD" &&
          (await runtime.GetAppStatisticsSummaryAsync("1h", 0)).ErrorCode == "INVALID_LIMIT" &&
          (await runtime.SetAppStatsRetentionAsync(5)).ErrorCode == "INVALID_RETENTION" &&
          (await runtime.GetAppStatsRetentionAsync()).ErrorCode == "APP_STATS_UNAVAILABLE",
          "real control-plane stable validation errors failed");
    Console.WriteLine("Query/IPC tests PASS (temporary DB and disconnected test pipe)");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

public class TestControlPlane : DispatchProxy
{
    public ApplicationTrafficPersistence? Store { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            "GetStatus" => new { service = "TEST" },
            "GetAppStatisticsSummaryAsync" => Summary((string)args![0]!, (int)args[1]!),
            "GetAppStatisticsDetailAsync" => Detail((string)args![0]!, (string)args[1]!),
            "GetAppStatsRetentionAsync" => Task.FromResult(ControlResult.Ok(new
            { retentionMinutes = Store!.RetentionMinutes, allowedValues = new[] { 60, 1440, 10080, 43200 } })),
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private async Task<ControlResult> Summary(string period, int limit)
    {
        if (!ApplicationTrafficPersistence.IsSupportedPeriod(period))
            return ControlResult.Error("INVALID_PERIOD", "Invalid period.");
        if (limit is < 1 or > 100) return ControlResult.Error("INVALID_LIMIT", "Invalid limit.");
        try { return ControlResult.Ok(await Store!.QuerySummaryAsync(period, limit)); }
        catch { return ControlResult.Error("APP_STATS_UNAVAILABLE", "Query failed."); }
    }

    private async Task<ControlResult> Detail(string appKey, string period)
    {
        if (!ApplicationTrafficPersistence.IsSupportedPeriod(period))
            return ControlResult.Error("INVALID_PERIOD", "Invalid period.");
        try { return ControlResult.Ok(await Store!.QueryDetailAsync(appKey, period)); }
        catch { return ControlResult.Error("APP_STATS_UNAVAILABLE", "Query failed."); }
    }
}
