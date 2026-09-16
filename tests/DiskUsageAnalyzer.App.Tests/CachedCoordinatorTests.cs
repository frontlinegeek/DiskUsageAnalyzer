using DiskUsageAnalyzer.App.Services;
using System.IO;
using DiskUsageAnalyzer.Core.Caching;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Infrastructure.Caching;
using Moq;

namespace DiskUsageAnalyzer.App.Tests;

public sealed class CachedCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dua-coordinator-cache-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "cache.db");
    private static readonly VolumeIdentity Volume = new("C:\\", 12, "NTFS");
    private static readonly JournalCheckpoint Checkpoint = new(8, 100, 1);

    [Fact]
    public async Task IncrementalRefresh_ReconcilesRenameMoveDeleteAndSizeThenUpdatesAggregates()
    {
        var cache = new SqliteScanCache(Database);
        var root = Tree();
        await cache.ReplaceAsync(root, Options(root.FullPath), Volume, Checkpoint, DateTimeOffset.UtcNow, null, default);
        var oldPath = root.Children[0].Children[0].FullPath;
        var deletedPath = root.Children[1].Children[0].FullPath;
        var movedPath = Path.Combine(root.Children[1].FullPath, "moved.bin");
        var journal = new FakeJournal(new(IncrementalReadStatus.Available, Checkpoint with { NextUsn = 140 },
        [
            new DiskUsageChange { Kind = DiskUsageChangeKind.Renamed, OldFullPath = oldPath, FullPath = movedPath },
            new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = deletedPath },
            new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = movedPath }
        ]));
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        snapshots.Setup(s => s.ReadSnapshot(movedPath)).Returns(new SnapshotResult(SnapshotStatus.Available,
            new DiskItem { Name = "moved.bin", FullPath = movedPath, ItemType = DiskItemType.File, LogicalSizeBytes = 15 }));
        using var session = new ScanSessionCoordinator(Mock.Of<IDiskScanner>(), Mock.Of<IDiskUsageExporter>(),
            Mock.Of<IFileSystemChangeMonitor>(), _ => snapshots.Object, cache, journal);

        Assert.True(await session.LoadCachedAsync(root.FullPath, null, default));
        var result = await session.RefreshCachedAsync(null, default);

        Assert.Equal(CacheRefreshKind.Incremental, result.Kind);
        var snapshot = session.Snapshot!;
        Assert.Equal(15, snapshot.Rows[root.FullPath].SizeBytes);
        Assert.Equal(0, snapshot.Rows[root.Children[0].FullPath].SizeBytes);
        Assert.Equal(15, snapshot.Rows[root.Children[1].FullPath].SizeBytes);
        Assert.Contains(movedPath, snapshot.Rows.Keys);
        Assert.DoesNotContain(oldPath, snapshot.Rows.Keys);
        Assert.DoesNotContain(deletedPath, snapshot.Rows.Keys);
        var persisted = await cache.LoadLatestAsync(root.FullPath, null, default);
        Assert.Equal(15, persisted!.Root.LogicalSizeBytes);
        Assert.Equal(140, persisted.Metadata.Journal!.NextUsn);
    }

    [Theory]
    [InlineData(IncrementalReadStatus.JournalChanged)]
    [InlineData(IncrementalReadStatus.JournalWrapped)]
    [InlineData(IncrementalReadStatus.Unsafe)]
    public async Task InvalidJournal_AutomaticallyFallsBackToFullScan(IncrementalReadStatus status)
    {
        var cache = new SqliteScanCache(Database);
        var cached = Tree();
        await cache.ReplaceAsync(cached, Options(cached.FullPath), Volume, Checkpoint, DateTimeOffset.UtcNow, null, default);
        var replacement = Tree();
        replacement.Children[0].Children[0].LogicalSizeBytes = 99;
        DiskTree.Recalculate(replacement.Children[0]); DiskTree.Recalculate(replacement);
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(replacement);
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(),
            Mock.Of<IFileSystemChangeMonitor>(), cache: cache,
            journal: new FakeJournal(new(status, Checkpoint, [], "invalid journal")));

        await session.LoadCachedAsync(cached.FullPath, null, default);
        var result = await session.RefreshCachedAsync(null, default);

        Assert.Equal(CacheRefreshKind.FullScan, result.Kind);
        Assert.Equal(109, session.Snapshot!.Rows[cached.FullPath].SizeBytes);
        scanner.Verify(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ScanOptions Options(string path) => new() { RootPath = path, IncludeHiddenItems = true };

    private static DiskItem Tree()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "dua-cached-root");
        var a = new DiskItem { Name = "a", FullPath = Path.Combine(rootPath, "a"), ItemType = DiskItemType.Directory };
        var b = new DiskItem { Name = "b", FullPath = Path.Combine(rootPath, "b"), ItemType = DiskItemType.Directory };
        a.Children.Add(new DiskItem { Name = "old.bin", FullPath = Path.Combine(a.FullPath, "old.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = 10 });
        b.Children.Add(new DiskItem { Name = "delete.bin", FullPath = Path.Combine(b.FullPath, "delete.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = 20 });
        var root = new DiskItem { Name = "root", FullPath = rootPath, ItemType = DiskItemType.Directory };
        root.Children.Add(a); root.Children.Add(b); DiskTree.Recalculate(a); DiskTree.Recalculate(b); DiskTree.Recalculate(root); return root;
    }

    private sealed class FakeJournal(IncrementalChangeSet changes) : IFileSystemJournal
    {
        public Task<(VolumeIdentity? Volume, JournalCheckpoint? Journal)> CaptureAsync(string rootPath, CancellationToken cancellationToken)
            => Task.FromResult<(VolumeIdentity?, JournalCheckpoint?)>((Volume, Checkpoint with { NextUsn = 200 }));
        public Task<IncrementalChangeSet> ReadAsync(CachedScan scan, CancellationToken cancellationToken) => Task.FromResult(changes);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
