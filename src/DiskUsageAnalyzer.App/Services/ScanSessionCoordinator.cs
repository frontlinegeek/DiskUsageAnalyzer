using System.IO;
using DiskUsageAnalyzer.Core.Caching;
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
    private readonly IScanCache? _cache;
    private readonly IFileSystemJournal? _journal;
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
    public CachedScanMetadata? CacheMetadata { get; private set; }
    public bool IsDisplayingCachedData { get; private set; }
    public event Action? ChangesPending;
    public event Action<string>? MonitoringFailed;

    public ScanSessionCoordinator(IDiskScanner scanner, IDiskUsageExporter exporter,
        IFileSystemChangeMonitor monitor, Func<ScanOptions, IDiskItemSnapshotProvider>? snapshots = null,
        IScanCache? cache = null, IFileSystemJournal? journal = null)
    {
        _scanner = scanner;
        _exporter = exporter;
        _monitor = monitor;
        _snapshots = snapshots ?? (options => new FileSystemDiskItemSnapshotProvider(options));
        _cache = cache;
        _journal = journal;
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
                IncludeSystemItems = options.IncludeSystemItems,
                CalculateAllocatedSize = options.CalculateAllocatedSize
            };
            lock (_pendingGate) _pending = new ChangeAccumulator(options.RootPath);
            // Listen before scanning so changes during enumeration are reconciled afterward.
            StartMonitoring();
            try
            {
                var checkpoint = _journal is null ? default : await _journal.CaptureAsync(options.RootPath, cancellation).ConfigureAwait(false);
                var root = await _scanner.ScanAsync(options, progress, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var snapshot = await Task.Run(() => ResultSnapshot.Create(root), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var completed = DateTimeOffset.UtcNow;
                if (_cache is not null)
                    await _cache.ReplaceAsync(root, _options, checkpoint.Volume, checkpoint.Journal, completed, progress, cancellation).ConfigureAwait(false);
                _root = root;
                Snapshot = snapshot;
                CacheMetadata = _cache is null ? null : await _cache.FindLatestAsync(root.FullPath, cancellation).ConfigureAwait(false);
                IsDisplayingCachedData = false;
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

    public async Task<bool> LoadCachedAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var loaded = false;
        await RunAsync(async cancellation =>
        {
            if (_cache is null) return;
            // Microsoft.Data.Sqlite can complete its async reads synchronously. Keep all database materialization
            // and hierarchy construction off the WPF dispatcher for caches with millions of entries.
            var cached = await Task.Run(() => _cache.LoadLatestAsync(rootPath, progress, cancellation), cancellation)
                .ConfigureAwait(false);
            if (cached is null) return;
            _monitor.Stop();
            Interlocked.Increment(ref _session);
            _root = cached.Root;
            _options = cached.Metadata.Options;
            CacheMetadata = cached.Metadata;
            IsDisplayingCachedData = true;
            Snapshot = await Task.Run(() => ResultSnapshot.Create(cached.Root), cancellation).ConfigureAwait(false);
            lock (_pendingGate) _pending = new ChangeAccumulator(cached.Root.FullPath);
            StartMonitoring();
            loaded = true;
        }, token).ConfigureAwait(false);
        return loaded;
    }

    public async Task<CacheRefreshResult> RefreshCachedAsync(IProgress<ScanProgress>? progress, CancellationToken token)
    {
        CacheRefreshResult? result = null;
        await RunAsync(async cancellation =>
        {
            if (_root is null || _options is null || CacheMetadata is null || _journal is null)
                throw new InvalidOperationException("No cached scan is loaded.");
            var cached = new CachedScan(CacheMetadata, _root);
            var changes = await _journal.ReadAsync(cached, cancellation).ConfigureAwait(false);
            if (changes.Status != IncrementalReadStatus.Available)
            {
                var detail = changes.Reason ?? "The journal cannot safely update this cache.";
                var checkpoint = await _journal.CaptureAsync(_options.RootPath, cancellation).ConfigureAwait(false);
                var root = await _scanner.ScanAsync(_options, progress, cancellation).ConfigureAwait(false);
                var completed = DateTimeOffset.UtcNow;
                if (_cache is not null) await _cache.ReplaceAsync(root, _options, checkpoint.Volume, checkpoint.Journal,
                    completed, progress, cancellation).ConfigureAwait(false);
                _root = root; Snapshot = ResultSnapshot.Create(root); IsDisplayingCachedData = false;
                CacheMetadata = _cache is null ? null : await _cache.FindLatestAsync(root.FullPath, cancellation).ConfigureAwait(false);
                result = new(CacheRefreshKind.FullScan, completed, detail);
                return;
            }
            if (changes.Changes.Count > 0)
            {
                var update = new DiskUsageDeltaUpdater().Apply(_root, changes.Changes, _snapshots(_options), cancellation);
                foreach (var path in MinimalPaths(update.RescanPaths))
                {
                    var replacement = await _scanner.ScanAsync(OptionsFor(path), progress, cancellation).ConfigureAwait(false);
                    if (!string.Equals(path, _root.FullPath, StringComparison.OrdinalIgnoreCase)
                        && replacement.Errors.Any(error => error.ExceptionType is nameof(DirectoryNotFoundException) or nameof(FileNotFoundException)))
                        new DiskUsageDeltaUpdater().Apply(_root,
                            [new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = path }], _snapshots(_options), cancellation);
                    else Replace(path, replacement);
                }
            }
            var updated = DateTimeOffset.UtcNow;
            if (_cache is not null)
                await _cache.UpdateAsync(CacheMetadata, _root, changes.Checkpoint, updated, progress, cancellation).ConfigureAwait(false);
            Snapshot = await Task.Run(() => ResultSnapshot.Create(_root), cancellation).ConfigureAwait(false);
            CacheMetadata = _cache is null ? CacheMetadata with { CompletedAt = updated, Journal = changes.Checkpoint }
                : await _cache.FindLatestAsync(_root.FullPath, cancellation).ConfigureAwait(false);
            IsDisplayingCachedData = false;
            result = new(CacheRefreshKind.Incremental, updated, $"Applied {changes.Changes.Count:N0} journal changes.");
        }, token).ConfigureAwait(false);
        return result!;
    }

    public Task RescanAsync(string path, IProgress<ScanProgress>? progress, CancellationToken token) => RunAsync(async cancellation =>
    {
        if (_root is null || _options is null) throw new InvalidOperationException("No current scan.");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!DiskTree.ContainsPath(_root.FullPath, path))
            throw new InvalidOperationException("The selected folder is outside the current scan.");
        var current = DiskTree.Enumerate(_root).SingleOrDefault(item =>
            string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (current is null || current.ItemType == DiskItemType.File || current.IsReparsePoint)
            throw new InvalidOperationException("Only scanned folders and drives can be rescanned.");

        var rescanningRoot = string.Equals(path, _root.FullPath, StringComparison.OrdinalIgnoreCase);
        // A subtree rescan does not reconcile changes elsewhere, so it must not advance the volume-wide USN checkpoint.
        var checkpoint = rescanningRoot && _journal is not null
            ? await _journal.CaptureAsync(_root.FullPath, cancellation).ConfigureAwait(false)
            : (CacheMetadata?.Volume, CacheMetadata?.Journal);
        var replacement = await _scanner.ScanAsync(OptionsFor(path), progress, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();

        var previousRoot = _root;
        if (string.Equals(path, _root.FullPath, StringComparison.OrdinalIgnoreCase)) _root = replacement;
        else Replace(path, replacement);
        ResultSnapshot? candidateSnapshot = null;
        var committed = false;
        try
        {
            candidateSnapshot = await Task.Run(() => ResultSnapshot.Create(_root), cancellation).ConfigureAwait(false);
            var completed = DateTimeOffset.UtcNow;
            if (_cache is not null)
            {
                if (CacheMetadata is null)
                    await _cache.ReplaceAsync(_root, _options, checkpoint.Item1,
                        rescanningRoot ? checkpoint.Item2 : null,
                        completed, progress, cancellation).ConfigureAwait(false);
                else
                    await _cache.UpdateAsync(CacheMetadata, _root, checkpoint.Item2,
                        completed, progress, cancellation).ConfigureAwait(false);
                committed = true;
                CacheMetadata = await _cache.FindLatestAsync(_root.FullPath, cancellation).ConfigureAwait(false);
            }
            else committed = true;
            Snapshot = candidateSnapshot;
            IsDisplayingCachedData = false;
        }
        catch
        {
            if (!committed)
            {
                if (string.Equals(path, previousRoot.FullPath, StringComparison.OrdinalIgnoreCase)) _root = previousRoot;
                else Replace(path, current);
            }
            else if (candidateSnapshot is not null) Snapshot = candidateSnapshot;
            throw;
        }
    }, token);

    public Task DeleteCacheAsync(string rootPath, CancellationToken token) => RunAsync(async cancellation =>
    {
        if (_cache is null) return;
        await _cache.DeleteAsync(rootPath, cancellation).ConfigureAwait(false);
        if (CacheMetadata is not null && string.Equals(CacheMetadata.RootPath,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)), StringComparison.OrdinalIgnoreCase))
        {
            CacheMetadata = null;
            IsDisplayingCachedData = false;
        }
    }, token);

    private static IReadOnlyList<string> MinimalPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths.OrderBy(path => path.Length))
            if (!result.Any(parent => DiskTree.ContainsPath(parent, path))) result.Add(path);
        return result;
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
        IncludeSystemItems = _options.IncludeSystemItems,
        CalculateAllocatedSize = _options.CalculateAllocatedSize
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
