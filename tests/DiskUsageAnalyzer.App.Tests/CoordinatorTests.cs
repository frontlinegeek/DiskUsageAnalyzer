using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using Moq;

namespace DiskUsageAnalyzer.App.Tests;

public sealed class CoordinatorTests
{
    private static readonly string RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dua-session");
    private static DiskItem Root(string? path = null) => new() { Name = "root", FullPath = path ?? RootPath, ItemType = DiskItemType.Directory };
    private static ScanOptions Options => new() { RootPath = RootPath };
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Watching_StartsBeforeScanAndRetainsEventsUntilRefresh()
    {
        var scanner = new Mock<IDiskScanner>();
        var monitor = new Mock<IFileSystemChangeMonitor>();
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        var started = false;
        monitor.Setup(m => m.Start(RootPath, It.IsAny<long>())).Callback(() => started = true);
        var file = new DiskItem { Name = "new.bin", FullPath = System.IO.Path.Combine(RootPath, "new.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = 23 };
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((ScanOptions _, IProgress<ScanProgress>? _, CancellationToken _) =>
            {
                Assert.True(started);
                monitor.Raise(m => m.ChangesReady += null, new ChangeBatch(1, [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = file.FullPath }]));
                return Task.FromResult(Root());
            });
        snapshots.Setup(s => s.ReadSnapshot(file.FullPath)).Returns(new SnapshotResult(SnapshotStatus.Available, file));
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), monitor.Object, _ => snapshots.Object);
        session.SetWatching(true);
        await session.ScanAsync(Options, null, default);
        Assert.True(session.HasPending);
        await session.RefreshAsync(default);
        Assert.False(session.HasPending);
        Assert.Equal(23, session.Snapshot!.Rows[RootPath].SizeBytes);
        monitor.Verify(m => m.Stop(), Times.Once);
    }

    [Fact]
    public async Task Export_IsSerializedBehindScan()
    {
        var scan = Completion<DiskItem>();
        var entered = Completion<bool>();
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(true); return scan.Task; });
        var exporter = new Mock<IDiskUsageExporter>();
        exporter.Setup(e => e.ExportAsync(It.IsAny<DiskItem>(), "report", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var session = new ScanSessionCoordinator(scanner.Object, exporter.Object, Mock.Of<IFileSystemChangeMonitor>());
        var scanning = session.ScanAsync(Options, null, default);
        await entered.Task;
        var exporting = session.ExportAsync("report", default);
        exporter.Verify(e => e.ExportAsync(It.IsAny<DiskItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        scan.SetResult(Root());
        await Task.WhenAll(scanning, exporting);
        exporter.Verify(e => e.ExportAsync(It.IsAny<DiskItem>(), "report", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispose_CancelsActiveScanAndRejectsPublication()
    {
        var entered = Completion<bool>();
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ScanOptions _, IProgress<ScanProgress>? _, CancellationToken token) =>
            { entered.SetResult(true); await Task.Delay(Timeout.Infinite, token); return Root(); });
        var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), Mock.Of<IFileSystemChangeMonitor>());
        var scanning = session.ScanAsync(Options, null, default);
        await entered.Task;
        session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanning);
        Assert.Null(session.Snapshot);
    }

    [Fact]
    public async Task NewRoot_RejectsPreviousSessionBatch()
    {
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((ScanOptions options, IProgress<ScanProgress>? _, CancellationToken _) => Task.FromResult(Root(options.RootPath)));
        var monitor = new Mock<IFileSystemChangeMonitor>();
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), monitor.Object);
        session.SetWatching(true);
        await session.ScanAsync(Options, null, default);
        await session.ScanAsync(new ScanOptions { RootPath = RootPath + "2" }, null, default);
        monitor.Raise(m => m.ChangesReady += null, new ChangeBatch(1,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Overflow, FullPath = RootPath }]));
        Assert.False(session.HasPending);
        Assert.Equal(RootPath + "2", session.Snapshot!.RootPath);
    }

    [Fact]
    public async Task Deletion_RejectsRootAndUnknownItemWithoutTouchingFilesystem()
    {
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Root());
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), Mock.Of<IFileSystemChangeMonitor>());
        await session.ScanAsync(Options, null, default);
        var actions = new Mock<IFileActions>(MockBehavior.Strict);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteAsync(RootPath, actions.Object, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteAsync(RootPath + "-outside", actions.Object, default));
    }

    [Fact]
    public async Task CanceledRootSwitch_RestoresPreviousSnapshotAndWatcherRoot()
    {
        var scanner = new Mock<IDiskScanner>();
        scanner.SetupSequence(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Root()).ThrowsAsync(new OperationCanceledException());
        var monitor = new Mock<IFileSystemChangeMonitor>();
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), monitor.Object);
        session.SetWatching(true);
        await session.ScanAsync(Options, null, default);
        var previous = session.Snapshot;
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.ScanAsync(new ScanOptions { RootPath = RootPath + "2" }, null, default));
        Assert.Same(previous, session.Snapshot);
        monitor.Verify(m => m.Start(RootPath, session.Session), Times.Once);
        Assert.True(session.HasPending);
    }

    [Fact]
    public async Task RefreshFailure_RetainsReconciliationWithoutSchedulingAnEndlessRetry()
    {
        var scanner = new Mock<IDiskScanner>();
        scanner.SetupSequence(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Root()).ThrowsAsync(new System.IO.IOException("Read failed"));
        var monitor = new Mock<IFileSystemChangeMonitor>();
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), monitor.Object);
        session.SetWatching(true);
        await session.ScanAsync(Options, null, default);
        session.QueueReconciliation();
        var notifications = 0;
        session.ChangesPending += () => notifications++;
        await Assert.ThrowsAsync<System.IO.IOException>(() => session.RefreshAsync(default));
        Assert.Equal(0, notifications);
        Assert.True(session.HasPending);
        Assert.Equal(1, session.Snapshot!.Rows[RootPath].ErrorCount);
    }

    [Fact]
    public async Task ChangesDuringRefresh_AreQueuedForTheNextBatch()
    {
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Root());
        var monitor = new Mock<IFileSystemChangeMonitor>();
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        var file = new DiskItem { Name = "file", FullPath = System.IO.Path.Combine(RootPath, "file"), ItemType = DiskItemType.File, LogicalSizeBytes = 10 };
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), monitor.Object, _ => snapshots.Object);
        session.SetWatching(true);
        await session.ScanAsync(Options, null, default);
        snapshots.Setup(s => s.ReadSnapshot(file.FullPath)).Callback(() => monitor.Raise(m => m.ChangesReady += null,
                new ChangeBatch(session.Session, [new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = file.FullPath }])))
            .Returns(new SnapshotResult(SnapshotStatus.Available, file));
        monitor.Raise(m => m.ChangesReady += null, new ChangeBatch(session.Session,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = file.FullPath }]));
        await session.RefreshAsync(default);
        Assert.True(session.HasPending);
        Assert.Equal(10, session.Snapshot!.Rows[RootPath].SizeBytes);
    }

    [Fact]
    public async Task RescanFolder_ReplacesOnlySelectedSubtreeAndRecalculatesAncestors()
    {
        var folderPath = System.IO.Path.Combine(RootPath, "folder");
        var siblingPath = System.IO.Path.Combine(RootPath, "sibling.bin");
        var originalFolder = new DiskItem { Name = "folder", FullPath = folderPath, ItemType = DiskItemType.Directory };
        originalFolder.Children.Add(new DiskItem { Name = "old.bin", FullPath = System.IO.Path.Combine(folderPath, "old.bin"),
            ItemType = DiskItemType.File, LogicalSizeBytes = 10 });
        DiskTree.Recalculate(originalFolder);
        var root = Root();
        root.Children.Add(originalFolder);
        root.Children.Add(new DiskItem { Name = "sibling.bin", FullPath = siblingPath,
            ItemType = DiskItemType.File, LogicalSizeBytes = 5 });
        DiskTree.Recalculate(root);
        var replacement = new DiskItem { Name = "folder", FullPath = folderPath, ItemType = DiskItemType.Directory };
        replacement.Children.Add(new DiskItem { Name = "new.bin", FullPath = System.IO.Path.Combine(folderPath, "new.bin"),
            ItemType = DiskItemType.File, LogicalSizeBytes = 30 });
        DiskTree.Recalculate(replacement);
        var scanner = new Mock<IDiskScanner>();
        scanner.SetupSequence(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(root).ReturnsAsync(replacement);
        using var session = new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), Mock.Of<IFileSystemChangeMonitor>());
        await session.ScanAsync(Options, null, default);

        await session.RescanAsync(folderPath, null, default);

        Assert.Equal(35, session.Snapshot!.Rows[RootPath].SizeBytes);
        Assert.Equal(30, session.Snapshot.Rows[folderPath].SizeBytes);
        Assert.Contains(siblingPath, session.Snapshot.Rows.Keys);
        Assert.DoesNotContain(System.IO.Path.Combine(folderPath, "old.bin"), session.Snapshot.Rows.Keys);
        scanner.Verify(s => s.ScanAsync(It.Is<ScanOptions>(o => o.RootPath == folderPath),
            It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
