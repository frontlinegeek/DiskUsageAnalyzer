using System.IO;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Infrastructure.Updating;

namespace DiskUsageAnalyzer.App.Services;

public sealed class ScanSessionCoordinator : IDisposable
{
    private readonly IDiskScanner _scanner;
    private readonly IDiskUsageExporter _exporter;
    private readonly IFileSystemChangeMonitor _monitor;
    private readonly Func<ScanOptions, IDiskItemSnapshotProvider> _snapshots;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _pendingGate = new();
    private ChangeAccumulator? _pending;
    private DiskItem? _root;
    private ScanOptions? _options;
    private long _session;
    private volatile bool _watch;
    private volatile bool _disposed;
    public long Session => Interlocked.Read(ref _session);
    public ResultSnapshot? Snapshot { get; private set; }
    public event Action? ChangesPending;
    public event Action<string>? MonitoringFailed;

    public ScanSessionCoordinator(IDiskScanner scanner, IDiskUsageExporter exporter,
        IFileSystemChangeMonitor monitor, Func<ScanOptions, IDiskItemSnapshotProvider>? snapshots = null)
    {
        _scanner = scanner;
        _exporter = exporter;
        _monitor = monitor;
        _snapshots = snapshots ?? (options => new FileSystemDiskItemSnapshotProvider(options));
        _monitor.ChangesReady += OnChanges;
    }

    public async Task ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        await RunAsync(async cancellation =>
        {
            var previousOptions = _options;
            _monitor.Stop();
            Interlocked.Increment(ref _session);
            _options = new ScanOptions
            {
                RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath)),
                IncludeHiddenItems = options.IncludeHiddenItems,
                IncludeSystemItems = options.IncludeSystemItems
            };
            lock (_pendingGate) _pending = new ChangeAccumulator(options.RootPath);
            // Listen before scanning so changes during enumeration are reconciled afterward.
            StartMonitoring();
            try
            {
                var root = await _scanner.ScanAsync(options, progress, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var snapshot = await Task.Run(() => ResultSnapshot.Create(root), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                _root = root;
                Snapshot = snapshot;
            }
            catch
            {
                _monitor.Stop();
                Interlocked.Increment(ref _session);
                _options = previousOptions;
                lock (_pendingGate) _pending = previousOptions is null ? null : new ChangeAccumulator(previousOptions.RootPath);
                StartMonitoring();
                QueueReconciliation(notify: false);
                throw;
            }
        }, token).ConfigureAwait(false);
    }

    public void SetWatching(bool enabled)
    {
        _watch = enabled;
        if (!enabled) { _monitor.Stop(); lock (_pendingGate) _pending?.Drain(); }
        else if (_options is not null)
        {
            StartMonitoring();
            QueueReconciliation();
        }
    }

    private void StartMonitoring()
    {
        if (!_watch || _disposed || _options is null) return;
        try { _monitor.Start(_options.RootPath, Session); }
        catch (Exception ex) { MonitoringFailed?.Invoke(ex.Message); }
    }

    private void OnChanges(ChangeBatch batch) => Enqueue(batch, true);

    private void Enqueue(ChangeBatch batch, bool notify)
    {
        lock (_pendingGate)
        {
            if (_disposed || !_watch || batch.Session != Session || _pending is null) return;
            foreach (var change in batch.Changes) _pending.Add(change, DateTimeOffset.UtcNow);
        }
        if (notify) ChangesPending?.Invoke();
    }

    public bool HasPending { get { lock (_pendingGate) return _pending?.Count > 0; } }

    public void QueueReconciliation(bool notify = true)
    {
        if (_options is not null) Enqueue(new ChangeBatch(Session,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Overflow, FullPath = _options.RootPath }]), notify);
    }

    public Task RefreshAsync(CancellationToken token) => RunAsync(async cancellation =>
    {
        if (_root is null || _options is null) return;
        IReadOnlyCollection<DiskUsageChange> changes;
        lock (_pendingGate) changes = _pending?.Drain() ?? [];
        if (changes.Count == 0) return;
        var root = _root;
        try
        {
            await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var result = new DiskUsageDeltaUpdater().Apply(root, changes, _snapshots(_options), cancellation);
                var paths = new List<string>();
                foreach (var path in result.RescanPaths.OrderBy(path => path.Length))
                    if (!paths.Any(parent => DiskTree.ContainsPath(parent, path))) paths.Add(path);
                foreach (var path in paths)
                {
                    var replacement = await _scanner.ScanAsync(OptionsFor(path), null, cancellation).ConfigureAwait(false);
                    if (!string.Equals(path, _root!.FullPath, StringComparison.OrdinalIgnoreCase)
                        && replacement.Errors.Any(error => error.ExceptionType is nameof(DirectoryNotFoundException) or nameof(FileNotFoundException)))
                        new DiskUsageDeltaUpdater().Apply(_root,
                            [new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = path }], _snapshots(_options));
                    else Replace(path, replacement);
                }
                var snapshot = ResultSnapshot.Create(_root!);
                cancellation.ThrowIfCancellationRequested();
                Snapshot = snapshot;
            }, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _root!.Errors.Add(new ScanError
                {
                    Path = _root.FullPath,
                    Message = "Live refresh incomplete: " + ex.Message,
                    ExceptionType = ex.GetType().Name
                });
                DiskTree.Recalculate(_root);
                Snapshot = ResultSnapshot.Create(_root);
            }
            QueueReconciliation(notify: false);
            throw;
        }
    }, token);

    private ScanOptions OptionsFor(string path) => new()
    {
        RootPath = path,
        IncludeHiddenItems = _options!.IncludeHiddenItems,
        IncludeSystemItems = _options.IncludeSystemItems
    };

    private void Replace(string path, DiskItem replacement)
    {
        if (_root is null) return;
        if (string.Equals(path, _root.FullPath, StringComparison.OrdinalIgnoreCase)) { _root = replacement; return; }
        var index = DiskTree.Enumerate(_root).ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
        var parentPath = Path.GetDirectoryName(path);
        if (parentPath is null || !index.TryGetValue(parentPath, out var parent)) return;
        var position = parent.Children.FindIndex(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (position < 0) return;
        parent.Children[position] = replacement;
        while (parent is not null)
        {
            DiskTree.Recalculate(parent);
            parent = Path.GetDirectoryName(parent.FullPath) is { } ancestor ? index.GetValueOrDefault(ancestor) : null;
        }
    }

    public Task ExportAsync(string path, CancellationToken token) => RunAsync(async cancellation =>
    {
        if (_root is not null) await _exporter.ExportAsync(_root, path, cancellation).ConfigureAwait(false);
    }, token);

    public Task DeleteAsync(string path, IFileActions actions, CancellationToken token) => RunAsync(async cancellation =>
    {
        if (_root is null) throw new InvalidOperationException("No current scan.");
        var item = DiskTree.Enumerate(_root).SingleOrDefault(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.ItemType == DiskItemType.Drive || item.IsReparsePoint
            || string.Equals(path, _root.FullPath, StringComparison.OrdinalIgnoreCase) || !DiskTree.ContainsPath(_root.FullPath, path))
            throw new InvalidOperationException("The selected item is not eligible for deletion.");
        await actions.DeleteAsync(_root.FullPath, path, item.ItemType, cancellation).ConfigureAwait(false);
        var parent = Path.GetDirectoryName(path)!;
        new DiskUsageDeltaUpdater().Apply(_root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = path }], _snapshots(_options!));
        Snapshot = ResultSnapshot.Create(_root);
        try
        {
            var refreshed = await _scanner.ScanAsync(OptionsFor(parent), null, cancellation).ConfigureAwait(false);
            Replace(parent, refreshed);
            Snapshot = await Task.Run(() => ResultSnapshot.Create(_root), cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            QueueReconciliation(notify: false);
            throw new IOException($"Deleted {path}; refreshing the remaining results failed: {ex.Message}", ex);
        }
    }, token);

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await action(linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _monitor.ChangesReady -= OnChanges;
        _monitor.Dispose();
        lock (_pendingGate) _pending = null;
    }
}
