using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Infrastructure.Updating;
using Moq;

namespace DiskUsageAnalyzer.Tests.Updating;

public sealed class SnapshotProviderTests
{
    [Theory]
    [InlineData(FileAttributes.Hidden, false, true, SnapshotStatus.Excluded)]
    [InlineData(FileAttributes.System, true, false, SnapshotStatus.Excluded)]
    [InlineData(FileAttributes.Hidden, true, true, SnapshotStatus.Available)]
    public void InclusionRules_MatchScanner(FileAttributes attributes, bool hidden, bool system, SnapshotStatus expected)
    {
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry("file")).Returns(new FileSystemEntry("file", attributes, 10, null));
        var provider = new FileSystemDiskItemSnapshotProvider(new ScanOptions { RootPath = ".", IncludeHiddenItems = hidden,
            IncludeSystemItems = system }, metadata.Object);
        Assert.Equal(expected, provider.ReadSnapshot("file").Status);
    }

    [Theory]
    [InlineData(true, SnapshotStatus.Missing)]
    [InlineData(false, SnapshotStatus.Inaccessible)]
    public void AccessAndMissing_AreDifferent(bool missing, SnapshotStatus expected)
    {
        var metadata = new Mock<IFileSystemMetadata>();
        metadata.Setup(m => m.GetEntry("file")).Throws(missing ? new FileNotFoundException() : new UnauthorizedAccessException());
        Assert.Equal(expected, new FileSystemDiskItemSnapshotProvider(metadata: metadata.Object).ReadSnapshot("file").Status);
    }
}
