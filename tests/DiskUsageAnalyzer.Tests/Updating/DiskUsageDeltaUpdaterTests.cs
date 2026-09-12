using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Updating;

namespace DiskUsageAnalyzer.Tests.Updating;

public sealed class DiskUsageDeltaUpdaterTests
{
    [Fact]
    public void Apply_DeletedKnownFile_RemovesFileAndSubtractsAncestorTotals()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = ChildFilePath }],
            new SnapshotProvider());

        Assert.False(result.RequiresRescan);
        Assert.Equal(1, result.AppliedChanges);
        Assert.DoesNotContain(root.Children[0].Children, child => child.FullPath == ChildFilePath);
        Assert.Equal(40, root.LogicalSizeBytes);
        Assert.Equal(1, root.FileCount);
    }

    [Fact]
    public void Apply_ChangedKnownFile_AdjustsSizeDelta()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();
        var snapshots = new SnapshotProvider();
        snapshots.Add(CreateFile(ChildFilePath, 25));

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = ChildFilePath }],
            snapshots);

        Assert.False(result.RequiresRescan);
        Assert.Equal(1, result.AppliedChanges);
        Assert.Equal(65, root.LogicalSizeBytes);
        Assert.Equal(25, DiskTree.Enumerate(root).Single(child => child.FullPath == ChildFilePath).LogicalSizeBytes);
    }

    [Fact]
    public void Apply_CreatedFileInKnownFolder_AddsFileAndAncestorTotals()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();
        var snapshots = new SnapshotProvider();
        var newPath = Path.Combine(ChildDirectoryPath, "new.bin");
        snapshots.Add(CreateFile(newPath, 15));

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = newPath }],
            snapshots);

        Assert.False(result.RequiresRescan);
        Assert.Equal(1, result.AppliedChanges);
        Assert.Contains(DiskTree.Enumerate(root), child => child.FullPath == newPath);
        Assert.Equal(65, root.LogicalSizeBytes);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(25, root.Children.Single(child => child.FullPath == ChildDirectoryPath).LogicalSizeBytes);
        Assert.Equal(2, root.Children.Single(child => child.FullPath == ChildDirectoryPath).FileCount);
    }

    [Fact]
    public void Apply_CreatedFileAtRoot_UpdatesRootTotals()
    {
        var root = CreateTree();
        var path = Path.Combine(RootPath, "new.bin");
        var snapshots = new SnapshotProvider();
        snapshots.Add(CreateFile(path, 15));
        new DiskUsageDeltaUpdater().Apply(root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = path }], snapshots);
        Assert.Equal(65, root.LogicalSizeBytes);
        Assert.Equal(3, root.FileCount);
    }

    [Fact]
    public void Apply_CreatedFileWithUnknownParent_RequestsRescanWithoutMisplacingFile()
    {
        var root = CreateTree();
        var path = Path.Combine(ChildDirectoryPath, "unknown", "new.bin");
        var snapshots = new SnapshotProvider();
        snapshots.Add(CreateFile(path, 15));
        var result = new DiskUsageDeltaUpdater().Apply(root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = path }], snapshots);
        Assert.True(result.RequiresRescan);
        Assert.Equal(50, root.LogicalSizeBytes);
        Assert.DoesNotContain(root.Children[0].Children, child => child.FullPath == path);
    }

    [Fact]
    public void Apply_RenamedFileWithinKnownFolder_UpdatesPathWithoutRescan()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();
        var newPath = Path.Combine(ChildDirectoryPath, "renamed.bin");
        var snapshots = new SnapshotProvider();
        snapshots.Add(CreateFile(newPath, 10));

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Renamed, OldFullPath = ChildFilePath, FullPath = newPath }],
            snapshots);

        Assert.False(result.RequiresRescan);
        Assert.Equal(1, result.AppliedChanges);
        Assert.DoesNotContain(DiskTree.Enumerate(root), child => child.FullPath == ChildFilePath);
        Assert.Contains(DiskTree.Enumerate(root), child => child.FullPath == newPath && child.Name == "renamed.bin");
        Assert.Equal(50, root.LogicalSizeBytes);
    }

    [Fact]
    public void Apply_CreatedDirectory_ReturnsParentRescanPath()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();
        var snapshots = new SnapshotProvider();
        var newDirectoryPath = Path.Combine(ChildDirectoryPath, "new-folder");
        snapshots.Add(new DiskItem
        {
            Name = "new-folder",
            FullPath = newDirectoryPath,
            ItemType = DiskItemType.Directory
        });

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = newDirectoryPath }],
            snapshots);

        Assert.True(result.RequiresRescan);
        Assert.Contains(ChildDirectoryPath, result.RescanPaths);
    }

    [Fact]
    public void Apply_DeletedKnownFolder_SubtractsWholeSubtree()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = ChildDirectoryPath }],
            new SnapshotProvider());

        Assert.False(result.RequiresRescan);
        Assert.Equal(1, result.AppliedChanges);
        Assert.DoesNotContain(root.Children, child => child.FullPath == ChildDirectoryPath);
        Assert.Equal(40, root.LogicalSizeBytes);
        Assert.Equal(1, root.FileCount);
        Assert.Equal(0, root.FolderCount);
    }

    [Fact]
    public void Apply_RenamedKnownFolder_PreservesPathsAndRequestsMetadataReconciliation()
    {
        var root = CreateTree();
        var updater = new DiskUsageDeltaUpdater();
        var newFolderPath = Path.Combine(RootPath, "renamed-child");
        var newChildFilePath = Path.Combine(newFolderPath, "child.bin");
        var snapshots = new SnapshotProvider();
        snapshots.Add(new DiskItem { Name = "renamed-child", FullPath = newFolderPath, ItemType = DiskItemType.Directory });

        var result = updater.Apply(
            root,
            [new DiskUsageChange { Kind = DiskUsageChangeKind.Renamed, OldFullPath = ChildDirectoryPath, FullPath = newFolderPath }],
            snapshots);

        Assert.Contains(newFolderPath, result.RescanPaths);
        Assert.Equal(1, result.AppliedChanges);
        var renamedFolder = Assert.Single(root.Children, child => child.FullPath == newFolderPath);
        Assert.Equal("renamed-child", renamedFolder.Name);
        Assert.Contains(renamedFolder.Children, child => child.FullPath == newChildFilePath);
        Assert.Equal(50, root.LogicalSizeBytes);
        Assert.Equal(2, root.FileCount);
        Assert.Equal(1, root.FolderCount);
    }

    private static readonly string RootPath = Path.Combine(Path.GetTempPath(), "dua-root");
    private static readonly string ChildDirectoryPath = Path.Combine(RootPath, "child");
    private static readonly string ChildFilePath = Path.Combine(ChildDirectoryPath, "child.bin");

    private static DiskItem CreateTree()
    {
        var rootFile = CreateFile(Path.Combine(RootPath, "root.bin"), 40);
        var childFile = CreateFile(ChildFilePath, 10);
        var childDirectory = new DiskItem
        {
            Name = "child",
            FullPath = ChildDirectoryPath,
            ItemType = DiskItemType.Directory,
            LogicalSizeBytes = 10,
            FileCount = 1
        };
        childDirectory.Children.Add(childFile);

        var root = new DiskItem
        {
            Name = "dua-root",
            FullPath = RootPath,
            ItemType = DiskItemType.Directory,
            LogicalSizeBytes = 50,
            FileCount = 2,
            FolderCount = 1
        };
        root.Children.Add(childDirectory);
        root.Children.Add(rootFile);
        return root;
    }

    private static DiskItem CreateFile(string path, long size)
    {
        return new DiskItem
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            ItemType = DiskItemType.File,
            LogicalSizeBytes = size
        };
    }

    private sealed class SnapshotProvider : IDiskItemSnapshotProvider
    {
        private readonly Dictionary<string, DiskItem> _items = new(StringComparer.OrdinalIgnoreCase);

        public void Add(DiskItem item)
        {
            _items[item.FullPath] = item;
        }

        public bool TryGetSnapshot(string fullPath, out DiskItem item)
        {
            return _items.TryGetValue(fullPath, out item!);
        }
    }
}
