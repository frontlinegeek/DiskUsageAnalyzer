using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Infrastructure.Scanning;
using Moq;

namespace DiskUsageAnalyzer.Tests.Scanning;

public sealed class ScannerFailureTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dua-fake");
    private static ScanOptions Options => new() { RootPath = Root };
    private static FileSystemEntry DirectoryEntry(string path) => new(path, FileAttributes.Directory, 0, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RootAccessFailure_ReturnsIncompleteResult(bool missing)
    {
        var metadata = new Mock<IFileSystemMetadata>(MockBehavior.Strict);
        metadata.Setup(m => m.GetEntry(Root)).Throws(missing ? new DirectoryNotFoundException() : new UnauthorizedAccessException());
        var result = await new FileSystemDiskScanner(metadata.Object).ScanAsync(Options, null, default);
        Assert.Single(result.Errors);
        Assert.Equal(1, result.SubtreeErrorCount);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task EnumerationFailure_PreservesReadEntriesAndDisposesEnumerator()
    {
        var disposed = false;
        IEnumerable<FileSystemEntry> Entries()
        {
            try
            {
                yield return new FileSystemEntry(Path.Combine(Root, "good.bin"), FileAttributes.Normal, 7, null);
                throw new IOException("Enumeration interrupted");
            }
            finally { disposed = true; }
        }
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry(Root)).Returns(DirectoryEntry(Root));
        metadata.Setup(m => m.Enumerate(Root)).Returns(Entries);
        var root = await new FileSystemDiskScanner(metadata.Object).ScanAsync(Options, null, default);
        Assert.True(disposed);
        Assert.Equal(7, root.LogicalSizeBytes);
        Assert.Single(root.Errors);
    }

    [Fact]
    public async Task Errors_AreOwnedOnceAndAggregated()
    {
        var child = Path.Combine(Root, "child");
        var file = Path.Combine(child, "bad.bin");
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry(Root)).Returns(DirectoryEntry(Root));
        metadata.Setup(m => m.Enumerate(Root)).Returns([DirectoryEntry(child)]);
        metadata.Setup(m => m.Enumerate(child)).Returns([new FileSystemEntry(file, FileAttributes.Normal, 0, null,
            new ScanError { Path = file, Message = "Denied" })]);
        var root = await new FileSystemDiskScanner(metadata.Object).ScanAsync(Options, null, default);
        Assert.Empty(root.Errors);
        Assert.Equal(1, root.SubtreeErrorCount);
        Assert.Equal(1, root.Children[0].SubtreeErrorCount);
        Assert.Single(root.Children[0].Children[0].Errors);
    }

    [Fact]
    public async Task HiddenSystemAndReparsePoints_RespectOptions()
    {
        var link = Path.Combine(Root, "link");
        var metadata = new Mock<IFileSystemMetadata>(MockBehavior.Strict);
        metadata.Setup(m => m.GetEntry(Root)).Returns(DirectoryEntry(Root));
        metadata.Setup(m => m.Enumerate(Root)).Returns([
            new FileSystemEntry(link, FileAttributes.Directory | FileAttributes.ReparsePoint, 0, null),
            new FileSystemEntry(Path.Combine(Root, "hidden"), FileAttributes.Hidden, 10, null),
            new FileSystemEntry(Path.Combine(Root, "system"), FileAttributes.System, 20, null)]);
        var root = await new FileSystemDiskScanner(metadata.Object).ScanAsync(Options, null, default);
        Assert.Single(root.Children);
        Assert.True(root.Children[0].IsReparsePoint);
        metadata.Verify(m => m.Enumerate(link), Times.Never);
    }

    [Fact]
    public async Task DeepTree_DoesNotUseCallStack()
    {
        const int depth = 3000;
        var paths = new string[depth];
        paths[0] = Root;
        for (var i = 1; i < depth; i++) paths[i] = Path.Combine(paths[i - 1], "d");
        var lookup = paths.Select((path, i) => (path, i)).ToDictionary(pair => pair.path, pair => pair.i);
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry(Root)).Returns(DirectoryEntry(Root));
        metadata.Setup(m => m.Enumerate(It.IsAny<string>())).Returns((string path) =>
            lookup[path] + 1 < depth ? new[] { DirectoryEntry(paths[lookup[path] + 1]) } : []);
        var root = await new FileSystemDiskScanner(metadata.Object).ScanAsync(Options, null, default);
        Assert.Equal(depth - 1, root.FolderCount);
    }

    [Fact]
    public async Task CancellationDuringEnumeration_DisposesEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        IEnumerable<FileSystemEntry> Entries()
        {
            try { cancellation.Cancel(); yield return DirectoryEntry(Path.Combine(Root, "child")); }
            finally { disposed = true; }
        }
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry(Root)).Returns(DirectoryEntry(Root));
        metadata.Setup(m => m.Enumerate(Root)).Returns(Entries);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSystemDiskScanner(metadata.Object).ScanAsync(Options, null, cancellation.Token));
        Assert.True(disposed);
    }

    [Fact]
    public async Task Progress_IsTimeBoundedAndFinalCountersAreExact()
    {
        var time = new ManualTimeProvider();
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry(Root)).Returns(DirectoryEntry(Root));
        metadata.Setup(m => m.Enumerate(Root)).Returns(Enumerable.Range(0, 1000)
            .Select(i => new FileSystemEntry(Path.Combine(Root, i.ToString()), FileAttributes.Normal, 1, null)));
        var updates = new List<ScanProgress>();
        await new FileSystemDiskScanner(metadata.Object, time).ScanAsync(Options, new InlineProgress<ScanProgress>(updates.Add), default);
        Assert.Equal(2, updates.Count);
        Assert.Equal(1000, updates[^1].FilesScanned);
        Assert.Equal(1000, updates[^1].BytesScanned);
    }

    [Theory]
    [InlineData(true, false)]
    public async Task UnsupportedOptions_AreExplicit(bool links, bool allocated) => await Assert.ThrowsAsync<NotSupportedException>(() =>
        new FileSystemDiskScanner().ScanAsync(new ScanOptions { RootPath = Root, FollowReparsePoints = links,
            CalculateAllocatedSize = allocated }, null, default));
}
