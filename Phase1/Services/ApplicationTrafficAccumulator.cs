using System.Collections.Concurrent;
using System.Threading;

namespace NetBinder.Service.Services;

/// <summary>Runtime-only payload accepted by DualWAN relay socket sends.</summary>
public sealed class ApplicationTrafficAccumulator
{
    public sealed record Snapshot(long MinuteUtc, string ApplicationPath, string Wan,
        long UploadBytes, long DownloadBytes, long TcpFlows, long UdpSessions);

    private sealed class Cell
    {
        public long Upload;
        public long Download;
        public long Tcp;
        public long Udp;
    }

    private sealed class Bucket(long minute)
    {
        public long Minute { get; } = minute;
        public readonly ConcurrentDictionary<(string Path, string Wan), Cell> Cells = new();
        public int Writers;
        public int Sealed;
    }

    public sealed class FlowCounter
    {
        private readonly ApplicationTrafficAccumulator _owner;
        private readonly string _path;
        private readonly string _wan;
        private int _udpStarted;

        internal FlowCounter(ApplicationTrafficAccumulator owner, string path, string wan)
        { _owner = owner; _path = path; _wan = wan; }

        public void Upload(int bytes) => _owner.TryRecord(_path, _wan, bytes, 0, 0, 0);
        public void Download(int bytes) => _owner.TryRecord(_path, _wan, 0, bytes, 0, 0);
        public void TcpConnected() => _owner.TryRecord(_path, _wan, 0, 0, 1, 0);
        public void UdpSent(int bytes)
        {
            if (bytes < 0) return;
            if (Interlocked.Exchange(ref _udpStarted, 1) == 0)
                _owner.TryRecord(_path, _wan, bytes, 0, 0, 1);
            else Upload(bytes);
        }
    }

    private const int RetainedMinutes = 4;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _rotation = new();
    private readonly LinkedList<Bucket> _buckets = new();
    private Bucket? _current;
    private long _lastSealedMinute = long.MinValue;
    private long _missedAttribution;
    private long _failedUpdates;
    private long _droppedBuckets;

    public ApplicationTrafficAccumulator(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public long MissedAttribution => Interlocked.Read(ref _missedAttribution);
    public long FailedUpdates => Interlocked.Read(ref _failedUpdates);
    public long DroppedBuckets => Interlocked.Read(ref _droppedBuckets);
    public int PendingSealedBuckets
    {
        get { lock (_rotation) return _buckets.Count(x => Volatile.Read(ref x.Sealed) != 0); }
    }

    public FlowCounter? CreateFlow(string? executablePath, string? actualWan)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !Path.IsPathFullyQualified(executablePath) ||
                !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                actualWan is not ("WAN1" or "WAN2"))
            {
                Interlocked.Increment(ref _missedAttribution);
                return null;
            }
            // Windows paths are case-insensitive. No filesystem access is needed.
            string path = Path.GetFullPath(executablePath.Replace('/', '\\'))
                .TrimEnd('\\').ToUpperInvariant();
            return new FlowCounter(this, path, actualWan);
        }
        catch
        {
            Interlocked.Increment(ref _missedAttribution);
            return null;
        }
    }

    private void TryRecord(string path, string wan, int upload, int download, int tcp, int udp)
    {
        if (upload < 0 || download < 0 || tcp < 0 || udp < 0) return;
        if ((upload | download | tcp | udp) == 0) return;
        try
        {
            long minute = _clock().ToUniversalTime().ToUnixTimeSeconds() / 60 * 60;
            Bucket bucket = Enter(minute);
            try
            {
                Cell cell = bucket.Cells.GetOrAdd((path, wan), static _ => new Cell());
                if (upload != 0) Interlocked.Add(ref cell.Upload, upload);
                if (download != 0) Interlocked.Add(ref cell.Download, download);
                if (tcp != 0) Interlocked.Add(ref cell.Tcp, tcp);
                if (udp != 0) Interlocked.Add(ref cell.Udp, udp);
            }
            finally { Interlocked.Decrement(ref bucket.Writers); }
        }
        catch { Interlocked.Increment(ref _failedUpdates); }
    }

    private Bucket Enter(long minute)
    {
        while (true)
        {
            Bucket? bucket = Volatile.Read(ref _current);
            if (bucket is not null && bucket.Minute == minute)
            {
                Interlocked.Increment(ref bucket.Writers);
                if (Volatile.Read(ref bucket.Sealed) == 0) return bucket;
                Interlocked.Decrement(ref bucket.Writers);
            }
            lock (_rotation)
            {
                bucket = _current;
                if (bucket is null || minute > bucket.Minute)
                {
                    if (bucket is not null)
                    {
                        Volatile.Write(ref bucket.Sealed, 1);
                        _lastSealedMinute = Math.Max(_lastSealedMinute, bucket.Minute);
                    }
                    minute = Math.Max(minute, _lastSealedMinute + 60);
                    bucket = new Bucket(minute);
                    _buckets.AddLast(bucket);
                    Volatile.Write(ref _current, bucket);
                    Trim();
                }
                else if (minute < bucket.Minute)
                {
                    // A delayed writer is charged to the current bucket rather than
                    // reopening a sealed minute that a later persistence step may flush.
                    minute = bucket.Minute;
                }
            }
        }
    }

    private void Trim()
    {
        while (_buckets.Count > RetainedMinutes && _buckets.First is { } first &&
               Volatile.Read(ref first.Value.Writers) == 0)
        {
            if (!first.Value.Cells.IsEmpty) Interlocked.Increment(ref _droppedBuckets);
            _buckets.RemoveFirst();
        }
    }

    /// <summary>Returns immutable completed minutes and releases them from RAM.
    /// Call with sealCurrent only after relay producers have stopped.</summary>
    public IReadOnlyList<IReadOnlyList<Snapshot>> CollectSealed(DateTimeOffset now, bool sealCurrent = false)
    {
        lock (_rotation)
        {
            long minute = now.ToUniversalTime().ToUnixTimeSeconds() / 60 * 60;
            if (_current is { } current && (sealCurrent || current.Minute < minute))
            {
                Volatile.Write(ref current.Sealed, 1);
                _lastSealedMinute = Math.Max(_lastSealedMinute, current.Minute);
                Volatile.Write(ref _current, null);
            }
            var result = new List<IReadOnlyList<Snapshot>>();
            for (var node = _buckets.First; node is not null;)
            {
                var next = node.Next;
                Bucket bucket = node.Value;
                if (Volatile.Read(ref bucket.Sealed) != 0 &&
                    Volatile.Read(ref bucket.Writers) == 0)
                {
                    var rows = bucket.Cells.Select(pair => new Snapshot(
                        bucket.Minute, pair.Key.Path, pair.Key.Wan,
                        Interlocked.Read(ref pair.Value.Upload), Interlocked.Read(ref pair.Value.Download),
                        Interlocked.Read(ref pair.Value.Tcp), Interlocked.Read(ref pair.Value.Udp))).ToArray();
                    if (rows.Length != 0) result.Add(rows);
                    _buckets.Remove(node);
                }
                node = next;
            }
            return result;
        }
    }

    /// <summary>Copies counters for tests and future persistence; never exposes cells.</summary>
    public IReadOnlyList<Snapshot> GetSnapshot()
    {
        lock (_rotation)
        {
            Trim();
            return _buckets.SelectMany(bucket => bucket.Cells.Select(pair => new Snapshot(
                bucket.Minute, pair.Key.Path, pair.Key.Wan,
                Interlocked.Read(ref pair.Value.Upload), Interlocked.Read(ref pair.Value.Download),
                Interlocked.Read(ref pair.Value.Tcp), Interlocked.Read(ref pair.Value.Udp)))).ToArray();
        }
    }
}
