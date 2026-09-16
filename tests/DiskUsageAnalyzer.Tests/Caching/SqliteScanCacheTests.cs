using DiskUsageAnalyzer.Core.Caching;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Infrastructure.Caching;

namespace DiskUsageAnalyzer.Tests.Caching;

public sealed class SqliteScanCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dua-cache-tests-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "cache.db");

    [Fact]
    public async Task ReplaceAndLoad_PreservesHierarchyMetadataAndErrors()
    {
        var cache = new SqliteScanCache(Database);
        var root = Tree("root-a", 23);
        var file = root.Children[0].Children[0];
        file.AllocatedSizeBytes = 4096;
        file.Attributes = FileAttributes.Archive | FileAttributes.Hidden;
        file.FileId = ulong.MaxValue - 3;
        file.ParentFileId = 42;
        file.Errors.Add(new ScanError { Path = file.FullPath, Message = "test", ExceptionType = "IOException" });
        DiskTree.Recalculate(root.Children[0]); DiskTree.Recalculate(root);
        var options = new ScanOptions { RootPath = root.FullPath, IncludeHiddenItems = true, CalculateAllocatedSize = true };
        var volume = new VolumeIdentity("C:\\", 123, "NTFS");
        var journal = new JournalCheckpoint(ulong.MaxValue - 7, 900, 100);

        await cache.ReplaceAsync(root, options, volume, journal, DateTimeOffset.Parse("2026-09-15T12:00:00Z"), null, default);
        var loaded = Assert.IsType<CachedScan>(await cache.LoadLatestAsync(root.FullPath, null, default));

        Assert.Equal(volume, loaded.Metadata.Volume);
        Assert.Equal(journal, loaded.Metadata.Journal);
        var loadedFile = loaded.Root.Children[0].Children[0];
        Assert.Equal(23, loaded.Root.LogicalSizeBytes);
        Assert.Equal(4096, loadedFile.AllocatedSizeBytes);
        Assert.Equal(file.Attributes, loadedFile.Attributes);
        Assert.Equal(file.FileId, loadedFile.FileId);
        Assert.Equal(file.ParentFileId, loadedFile.ParentFileId);
        Assert.Equal("test", Assert.Single(loadedFile.Errors).Message);
    }

    [Fact]
    public async Task MultipleRoots_AreIndependentAndCanBeDeleted()
    {
        var cache = new SqliteScanCache(Database);
        var first = Tree("first", 1); var second = Tree("second", 2);
        await Save(cache, first); await Save(cache, second);
        await cache.DeleteAsync(first.FullPath, default);
        Assert.Null(await cache.LoadLatestAsync(first.FullPath, null, default));
        Assert.Equal(2, (await cache.LoadLatestAsync(second.FullPath, null, default))!.Root.LogicalSizeBytes);
    }

    [Fact]
    public async Task CanceledReplacement_RollsBackAndKeepsPreviousValidScan()
    {
        var cache = new SqliteScanCache(Database);
        var original = Tree("stable", 7);
        await Save(cache, original);
        var replacement = Tree("stable", 1);
        var folder = replacement.Children[0];
        for (var i = 0; i < 5000; i++) folder.Children.Add(new DiskItem { Name = $"f{i}",
            FullPath = Path.Combine(folder.FullPath, $"f{i}"), ItemType = DiskItemType.File, LogicalSizeBytes = 1 });
        DiskTree.Recalculate(folder); DiskTree.Recalculate(replacement);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReplaceAsync(replacement,
            new ScanOptions { RootPath = replacement.FullPath }, null, null, DateTimeOffset.UtcNow, progress, cancellation.Token));
        var loaded = await cache.LoadLatestAsync(original.FullPath, null, default);
        Assert.Equal(7, loaded!.Root.LogicalSizeBytes);
    }

    private static Task Save(IScanCache cache, DiskItem root) => cache.ReplaceAsync(root,
        new ScanOptions { RootPath = root.FullPath }, null, null, DateTimeOffset.UtcNow, null, default);

    private static DiskItem Tree(string name, long size)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        var folder = new DiskItem { Name = "folder", FullPath = Path.Combine(path, "folder"), ItemType = DiskItemType.Directory };
        folder.Children.Add(new DiskItem { Name = "file.bin", FullPath = Path.Combine(folder.FullPath, "file.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = size });
        var root = new DiskItem { Name = name, FullPath = path, ItemType = DiskItemType.Directory };
        root.Children.Add(folder); DiskTree.Recalculate(folder); DiskTree.Recalculate(root); return root;
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => cancellation.Cancel(); }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
