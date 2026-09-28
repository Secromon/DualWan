using Microsoft.Data.Sqlite;
using NetBinder.Service.Services;
using System.Diagnostics;

static void Check(bool yes, string reason)
{
    if (!yes) throw new Exception(reason);
}

static async Task<long> Scalar(string path, string sql)
{
    await using var db = new SqliteConnection($"Data Source={path}");
    await db.OpenAsync();
    await using var command = db.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task Exec(string path, string sql)
{
    await using var db = new SqliteConnection($"Data Source={path}");
    await db.OpenAsync();
    await using var command = db.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

string folder = Path.Combine(Path.GetTempPath(), "DualWAN-AppStats-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    DateTimeOffset now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    string path = Path.Combine(folder, "telemetry.db");
    var traffic = new ApplicationTrafficAccumulator(() => now);
    var store = new ApplicationTrafficPersistence(path, traffic);
    await store.InitializeAsync();
    Check(store.Enabled && store.RetentionMinutes == 60, "P1/P8 fresh schema/default failed");
    Check(await Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_traffic_minute'") == 1 &&
          await Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_app_traffic_app_ts'") == 1,
          "P1 schema/index missing");
    await Exec(path, "CREATE TABLE raw_samples(ts INTEGER); INSERT INTO raw_samples VALUES(1234)");
    await store.InitializeAsync();
    Check(store.Enabled && await Scalar(path, "SELECT ts FROM raw_samples") == 1234,
        "P2/P3 additive idempotent migration failed");

    var a1 = traffic.CreateFlow(@"C:\A\helper.exe", "WAN1")!;
    var a2 = traffic.CreateFlow(@"C:\B\helper.exe", "WAN1")!;
    var aWan2 = traffic.CreateFlow(@"C:\A\helper.exe", "WAN2")!;
    a1.Upload(1000); a1.Download(500); a1.TcpConnected(); a1.UdpSent(200);
    a2.Upload(33); aWan2.Download(44);
    now = now.AddMinutes(1);
    var flushWatch = Stopwatch.StartNew();
    await store.FlushAsync(now);
    flushWatch.Stop();
    Console.WriteLine($"Measured first flush: 3 rows, {flushWatch.Elapsed.TotalMilliseconds:F2} ms, DB={new FileInfo(path).Length} B (synthetic)");
    Check(store.PendingBatches == 0 && await Scalar(path, "SELECT COUNT(*) FROM app_traffic_minute") == 3,
        "P4/P5 flush rows failed");
    Check(await Scalar(path, "SELECT upload_bytes FROM app_traffic_minute WHERE app_key LIKE '%\\A\\HELPER.EXE' AND wan='WAN1'") == 1200 &&
          await Scalar(path, "SELECT download_bytes FROM app_traffic_minute WHERE app_key LIKE '%\\A\\HELPER.EXE' AND wan='WAN1'") == 500 &&
          await Scalar(path, "SELECT tcp_flows FROM app_traffic_minute WHERE app_key LIKE '%\\A\\HELPER.EXE' AND wan='WAN1'") == 1 &&
          await Scalar(path, "SELECT udp_sessions FROM app_traffic_minute WHERE app_key LIKE '%\\A\\HELPER.EXE' AND wan='WAN1'") == 1,
          "P4 values wrong");
    // Replaying the same absolute row must replace, not add.
    await Exec(path, "UPDATE app_traffic_minute SET upload_bytes=999 WHERE wan='WAN1' AND app_key LIKE '%\\A\\HELPER.EXE'");
    // Simulate a retry by reintroducing the same sealed minute via a fresh accumulator/store.
    DateTimeOffset retryClock = now.AddMinutes(-1);
    var retryTraffic = new ApplicationTrafficAccumulator(() => retryClock);
    var retry = new ApplicationTrafficPersistence(path, retryTraffic);
    await retry.InitializeAsync();
    var retryFlow = retryTraffic.CreateFlow(@"C:\A\helper.exe", "WAN1")!;
    retryFlow.Upload(1200); retryFlow.Download(500); retryFlow.TcpConnected(); retryFlow.UdpSent(0);
    await retry.FlushAsync(now);
    Check(await Scalar(path, "SELECT upload_bytes FROM app_traffic_minute WHERE wan='WAN1' AND app_key LIKE '%\\A\\HELPER.EXE'") == 1200,
        "P6 retry doubled or did not replace");
    a1.Upload(5);
    now = now.AddMinutes(1);
    await store.FlushAsync(now);
    Check(await Scalar(path, "SELECT COUNT(*) FROM app_traffic_minute WHERE app_key LIKE '%\\A\\HELPER.EXE' AND wan='WAN1'") == 2,
        "P7 next minute did not create separate row");

    // All supported retentions, boundary behavior, and transitions.
    foreach (int minutes in new[] { 60, 1440, 10080, 43200 })
    {
        await store.SetRetentionAsync(minutes);
        Check(store.RetentionMinutes == minutes, "P9-P12 setting failed");
        long cutoff = now.ToUnixTimeSeconds() - minutes * 60L;
        await Exec(path, $"INSERT OR REPLACE INTO app_traffic_minute VALUES({cutoff - 60},'OLD{minutes}','WAN1',1,0,0,0),({cutoff},'BOUND{minutes}','WAN1',1,0,0,0)");
        var retentionWatch = Stopwatch.StartNew();
        await store.MaintainAsync(now, null);
        retentionWatch.Stop();
        if (minutes == 60) Console.WriteLine($"Measured 1h cleanup: {retentionWatch.Elapsed.TotalMilliseconds:F2} ms (synthetic)");
        Check(await Scalar(path, $"SELECT COUNT(*) FROM app_traffic_minute WHERE app_key='OLD{minutes}'") == 0 &&
              await Scalar(path, $"SELECT COUNT(*) FROM app_traffic_minute WHERE app_key='BOUND{minutes}'") == 1,
              "P9-P12 retention boundary failed");
    }
    await store.SetRetentionAsync(123);
    Check(store.RetentionMinutes == 60, "P13 invalid retention fallback failed");
    await store.SetRetentionAsync(43200);
    await Exec(path, $"INSERT OR REPLACE INTO app_traffic_minute VALUES({now.ToUnixTimeSeconds() - 7200},'TRANSITION','WAN1',1,0,0,0)");
    await store.SetRetentionAsync(60);
    await store.MaintainAsync(now, null);
    Check(await Scalar(path, "SELECT COUNT(*) FROM app_traffic_minute WHERE app_key='TRANSITION'") == 0,
        "P14 reduction failed");
    await store.SetRetentionAsync(43200);
    Check(await Scalar(path, "SELECT COUNT(*) FROM app_traffic_minute WHERE app_key='TRANSITION'") == 0,
        "P15 deleted history reconstructed");

    // Failed write stays bounded and cannot interrupt a synthetic relay send.
    await Exec(path, "DROP TABLE app_traffic_minute");
    for (int i = 0; i < 7; i++)
    {
        a1.Upload(10);
        now = now.AddMinutes(1);
        await store.FlushAsync(now);
    }
    Check(store.PendingBatches <= 4 && store.DroppedBatches >= 3,
        "P16/P19 failed write backlog unbounded");
    await TransparentProxy.SendFullyAsync(new byte[10], (data, _) => ValueTask.FromResult(data.Length), a1.Upload);
    await store.MaintainAsync(now, null); // P17: retention failure is contained.
    await store.InitializeAsync();
    await store.FlushAsync(now);
    Check(store.PendingBatches == 0, "P35 recovery failed");

    string broken = Path.Combine(folder, "broken.db");
    await Exec(broken, "CREATE TABLE app_traffic_minute(x INTEGER)");
    var badSchema = new ApplicationTrafficPersistence(broken, traffic);
    await badSchema.InitializeAsync();
    Check(!badSchema.Enabled, "P18 malformed schema unexpectedly enabled");
    await TransparentProxy.SendFullyAsync(new byte[10], (data, _) => ValueTask.FromResult(data.Length), a1.Upload);

    // Pending current minute on stop; failure on stop remains best effort.
    a1.Upload(77);
    await store.FlushAsync(now, sealCurrent: true);
    Check(await Scalar(path, "SELECT COUNT(*) FROM app_traffic_minute") > 0, "P20 final flush failed");
    await Exec(path, "DROP TABLE app_traffic_minute");
    a1.Upload(1);
    await store.FlushAsync(now.AddMinutes(1), sealCurrent: true);
    Check(store.PendingBatches > 0, "P21 final failure did not remain best effort");

    // Quota pruning, including a fixed maximum of 20 x 1000 rows per pass.
    string quotaPath = Path.Combine(folder, "quota.db");
    var quotaStore = new ApplicationTrafficPersistence(quotaPath, new ApplicationTrafficAccumulator());
    await quotaStore.InitializeAsync();
    await using (var db = new SqliteConnection($"Data Source={quotaPath}"))
    {
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using var command = db.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText = "INSERT INTO app_traffic_minute VALUES($ts,$key,'WAN1',1,0,0,0)";
        var ts = command.Parameters.Add("$ts", SqliteType.Integer);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        for (int i = 0; i < 21_000; i++) { ts.Value = now.ToUnixTimeSeconds(); key.Value = "APP" + i; await command.ExecuteNonQueryAsync(); }
        await tx.CommitAsync();
    }
    await quotaStore.MaintainAsync(now, 1);
    long remaining = await Scalar(quotaPath, "SELECT COUNT(*) FROM app_traffic_minute");
    Check(remaining is > 0 and <= 1000, "P22/P23 quota pruning not bounded at 20,000 rows");
    Console.WriteLine("Persistence tests PASS (temporary SQLite; no live routing)");
}
finally
{
    // Test-owned temporary database files only; no installed DualWAN data touched.
    SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}
