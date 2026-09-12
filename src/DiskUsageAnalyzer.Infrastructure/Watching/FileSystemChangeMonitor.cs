using DiskUsageAnalyzer.Core.Updating;

namespace DiskUsageAnalyzer.Infrastructure.Watching;

public sealed class FileSystemChangeMonitor : IFileSystemChangeMonitor
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<string, IFileSystemEventSource> _factory;
    private IFileSystemEventSource? _source;
    private ITimer? _timer;
    private ChangeAccumulator? _pending;
    private long _generation;
    private long _session;
    private bool _disposed;
    public event Action<ChangeBatch>? ChangesReady;
    public bool IsWatching { get { lock (_gate) return _source is not null; } }

    public FileSystemChangeMonitor(TimeProvider? time = null, Func<string, IFileSystemEventSource>? factory = null)
    {
        _time = time ?? TimeProvider.System;
        _factory = factory ?? (root => new FileSystemEventSource(root));
    }

    public void Start(string rootPath, long session)
    {
        Stop();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = new ChangeAccumulator(rootPath);
            _session = session;
            var generation = ++_generation;
            var source = _factory(rootPath);
            _source = source;
            _timer = _time.CreateTimer(_ => Flush(generation), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            source.Changed += change => Enqueue(generation, change);
            try { source.Start(); }
            catch { _source = null; source.Dispose(); _timer.Dispose(); _timer = null; throw; }
        }
    }

    private void Enqueue(long generation, DiskUsageChange change)
    {
        lock (_gate)
        {
            if (_disposed || _source is null || generation != _generation) return;
            var now = _time.GetUtcNow();
            _pending!.Add(change, now);
            _timer!.Change(_pending.DueIn(now), Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush(long generation)
    {
        ChangeBatch batch;
        lock (_gate)
        {
            if (_disposed || _source is null || generation != _generation || _pending!.Count == 0) return;
            var due = _pending.DueIn(_time.GetUtcNow());
            if (due > TimeSpan.Zero) { _timer!.Change(due, Timeout.InfiniteTimeSpan); return; }
            batch = new ChangeBatch(_session, _pending.Drain());
        }
        ChangesReady?.Invoke(batch);
    }

    public void Stop()
    {
        IFileSystemEventSource? source;
        lock (_gate)
        {
            _generation++;
            source = _source;
            _source = null;
            _timer?.Dispose();
            _timer = null;
            _pending = null;
        }
        source?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Stop();
    }
}
