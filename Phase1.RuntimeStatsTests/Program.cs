using NetBinder.Service.Services;

static void Check(bool yes, string reason)
{
    if (!yes) throw new Exception(reason);
}

DateTimeOffset now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
var stats = new ApplicationTrafficAccumulator(() => now);
var a1 = stats.CreateFlow(@"C:\TestA\helper.exe", "WAN1") ?? throw new Exception("identity unavailable");
var a2 = stats.CreateFlow(@"c:/testa/HELPER.EXE", "WAN1") ?? throw new Exception("identity unavailable");
var b1 = stats.CreateFlow(@"C:\TestB\helper.exe", "WAN1") ?? throw new Exception("identity unavailable");
var aWan2 = stats.CreateFlow(@"C:\TestA\helper.exe", "WAN2") ?? throw new Exception("identity unavailable");

var payload = new byte[1_000_000];
int calls = 0;
await TransparentProxy.SendFullyAsync(payload, (chunk, _) =>
{
    calls++;
    return ValueTask.FromResult(Math.Min(chunk.Length, 400_000));
}, a1.Upload);
Check(calls == 3, "partial send did not retry");
await TransparentProxy.SendFullyAsync(new byte[500_000], (chunk, _) =>
    ValueTask.FromResult(chunk.Length), a1.Download);
a1.TcpConnected();

try
{
    calls = 0;
    await TransparentProxy.SendFullyAsync(new byte[1000], (chunk, _) =>
    {
        calls++;
        if (calls == 2) throw new IOException("injected send failure");
        return ValueTask.FromResult(400);
    }, a1.Upload);
    throw new Exception("failure not propagated");
}
catch (IOException ex) when (ex.Message == "injected send failure") { }

for (int i = 0; i < 100; i++)
    await UdpRelay.SendDatagramAsync(new byte[1000], (data, _) =>
        ValueTask.FromResult(data.Length), a1.UdpSent);
for (int i = 0; i < 100; i++)
    await UdpRelay.SendDatagramAsync(new byte[1000], (data, _) =>
        ValueTask.FromResult(data.Length), a1.Download);
try
{
    await UdpRelay.SendDatagramAsync(new byte[1000], (_, _) =>
        ValueTask.FromResult(500), a1.Upload);
    throw new Exception("partial datagram not rejected");
}
catch (IOException ex) when (ex.Message.Contains("datagram")) { }

a2.Upload(7);
b1.Upload(11);
aWan2.Upload(13);
aWan2.TcpConnected();
var first = stats.GetSnapshot();
Check(first.Count == 3, "application/WAN cells merged incorrectly");
var a = first.Single(x => x.Wan == "WAN1" && x.ApplicationPath.Contains("TESTA"));
Check(a.UploadBytes == 1_100_407 && a.DownloadBytes == 600_000 &&
      a.TcpFlows == 1 && a.UdpSessions == 1, "known payload/flow totals incorrect");
Check(first.Single(x => x.Wan == "WAN1" && x.ApplicationPath.Contains("TESTB")).UploadBytes == 11,
    "same basename merged across paths");
Check(first.Single(x => x.Wan == "WAN2").UploadBytes == 13, "actual WAN separation failed");

now = now.AddMinutes(1);
a1.Upload(17);
Check(stats.GetSnapshot().Single(x => x.MinuteUtc == now.ToUnixTimeSeconds()).UploadBytes == 17,
    "minute rotation failed");
var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
{
    for (int i = 0; i < 10_000; i++) { a1.Upload(3); a1.Download(2); }
})).ToArray();
await Task.WhenAll(tasks);
var concurrent = stats.GetSnapshot().Single(x => x.MinuteUtc == now.ToUnixTimeSeconds());
Check(concurrent.UploadBytes == 240_017 && concurrent.DownloadBytes == 160_000,
    "concurrent updates lost increments");

Check(stats.CreateFlow(null, "WAN1") is null && stats.MissedAttribution == 1,
    "missing identity created a fake app");
var failedStats = new ApplicationTrafficAccumulator(() => throw new InvalidOperationException("clock failure"));
var failedFlow = failedStats.CreateFlow(@"C:\TestA\helper.exe", "WAN1")!;
await TransparentProxy.SendFullyAsync(new byte[10], (data, _) =>
    ValueTask.FromResult(data.Length), failedFlow.Upload);
Check(failedStats.FailedUpdates == 1, "accounting failure was not isolated");

for (int i = 0; i < 20; i++)
{
    now = now.AddMinutes(1);
    a1.Upload(1);
}
Check(stats.GetSnapshot().Select(x => x.MinuteUtc).Distinct().Count() <= 4,
    "old runtime buckets retained without bound");
Console.WriteLine("Runtime statistics tests PASS (synthetic sends; no live routing)");
