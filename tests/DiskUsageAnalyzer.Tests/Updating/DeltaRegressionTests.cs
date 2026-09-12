using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Updating;
using Moq;

namespace DiskUsageAnalyzer.Tests.Updating;

public sealed class DeltaRegressionTests
{
    private static readonly string RootPath = Path.Combine(Path.GetTempPath(), "dua-tree");
    private static DiskItem Root() => new() { Name = "root", FullPath = RootPath, ItemType = DiskItemType.Directory };
    private static DiskItem File(string name, long size) => new() { Name = name, FullPath = Path.Combine(RootPath, name),
        ItemType = DiskItemType.File, LogicalSizeBytes = size };

    [Theory]
    [InlineData(SnapshotStatus.Inaccessible, 10)]
    [InlineData(SnapshotStatus.Missing, 0)]
    [InlineData(SnapshotStatus.Excluded, 0)]
    public void SnapshotStatus_DistinguishesFailureFromDeletion(SnapshotStatus status, long expected)
    {
        var root = Root();
        var file = File("file", 10);
        root.Children.Add(file);
        DiskTree.Recalculate(root);
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        snapshots.Setup(s => s.ReadSnapshot(file.FullPath)).Returns(new SnapshotResult(status,
            Error: status == SnapshotStatus.Inaccessible ? new ScanError { Path = file.FullPath, Message = "Denied" } : null));
        var result = new DiskUsageDeltaUpdater().Apply(root, [new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = file.FullPath }], snapshots.Object);
        Assert.Equal(expected, root.LogicalSizeBytes);
        Assert.Equal(status == SnapshotStatus.Inaccessible, result.RequiresRescan);
        Assert.Equal(status == SnapshotStatus.Inaccessible ? 1 : 0, root.SubtreeErrorCount);
    }

    [Fact]
    public void DuplicateCreates_AreIdempotentAndSorted()
    {
        var root = Root();
        root.Children.Add(File("small", 1));
        DiskTree.Recalculate(root);
        var file = File("large", 100);
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        snapshots.Setup(s => s.ReadSnapshot(file.FullPath)).Returns(new SnapshotResult(SnapshotStatus.Available, file));
        var change = new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = file.FullPath };
        new DiskUsageDeltaUpdater().Apply(root, [change, change], snapshots.Object);
        Assert.Equal(101, root.LogicalSizeBytes);
        Assert.Equal(2, root.FileCount);
        Assert.Equal("large", root.Children[0].Name);
    }

    [Fact]
    public void SiblingPrefix_DoesNotEnterScannedRoot()
    {
        var snapshots = new Mock<IDiskItemSnapshotProvider>(MockBehavior.Strict);
        var root = Root();
        new DiskUsageDeltaUpdater().Apply(root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = RootPath + "-other/file" }], snapshots.Object);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void RenameIntoExcludedState_RemovesOldEntry()
    {
        var root = Root();
        var file = File("old", 10);
        root.Children.Add(file);
        DiskTree.Recalculate(root);
        var newPath = Path.Combine(RootPath, "new");
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        snapshots.Setup(s => s.ReadSnapshot(newPath)).Returns(new SnapshotResult(SnapshotStatus.Excluded));
        new DiskUsageDeltaUpdater().Apply(root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Renamed, OldFullPath = file.FullPath, FullPath = newPath }], snapshots.Object);
        Assert.Empty(root.Children);
        Assert.Equal(0, root.LogicalSizeBytes);
    }

    [Fact]
    public void FailedBatch_PreservesConsistentTotalsForAlreadyAppliedChanges()
    {
        var root = Root();
        var file = File("good", 10);
        var snapshots = new Mock<IDiskItemSnapshotProvider>();
        snapshots.Setup(s => s.ReadSnapshot(file.FullPath)).Returns(new SnapshotResult(SnapshotStatus.Available, file));
        snapshots.Setup(s => s.ReadSnapshot(Path.Combine(RootPath, "bad"))).Throws(new IOException("Failure"));
        Assert.Throws<IOException>(() => new DiskUsageDeltaUpdater().Apply(root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = file.FullPath },
                new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = Path.Combine(RootPath, "bad") }], snapshots.Object));
        Assert.Equal(10, root.LogicalSizeBytes);
        Assert.Equal(1, root.FileCount);
    }
}
