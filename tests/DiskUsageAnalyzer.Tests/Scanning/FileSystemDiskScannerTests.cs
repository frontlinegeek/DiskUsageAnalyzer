using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Infrastructure.Scanning;

namespace DiskUsageAnalyzer.Tests.Scanning;

public sealed class FileSystemDiskScannerTests
{
    [Fact]
    public async Task ScanAsync_ReturnsEmptyDirectoryResult()
    {
        using var temp = TemporaryDirectory.Create();
        var scanner = new FileSystemDiskScanner();

        var result = await scanner.ScanAsync(CreateOptions(temp.Path), null, CancellationToken.None);

        Assert.Equal(DiskItemType.Directory, result.ItemType);
        Assert.Equal(0, result.LogicalSizeBytes);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.FolderCount);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task ScanAsync_AccumulatesNestedFilesAndFolders()
    {
        using var temp = TemporaryDirectory.Create();
        var nested = Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "root.bin"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "child.bin"), new byte[25]);
        var scanner = new FileSystemDiskScanner();

        var result = await scanner.ScanAsync(CreateOptions(temp.Path), null, CancellationToken.None);

        Assert.Equal(35, result.LogicalSizeBytes);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(1, result.FolderCount);
        Assert.Equal(["nested", "root.bin"], result.Children.Select(child => child.Name).ToArray());
    }

    [Fact]
    public async Task ScanAsync_ReportsProgress()
    {
        using var temp = TemporaryDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "root.bin"), new byte[10]);
        var scanner = new FileSystemDiskScanner();
        var updates = new List<ScanProgress>();

        await scanner.ScanAsync(CreateOptions(temp.Path), new InlineProgress<ScanProgress>(updates.Add), CancellationToken.None);

        Assert.Contains(updates, update => update.FilesScanned == 1 && update.BytesScanned == 10);
    }

    [Fact]
    public async Task ScanAsync_HonorsCancellation()
    {
        using var temp = TemporaryDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var scanner = new FileSystemDiskScanner();

        await Assert.ThrowsAsync<TaskCanceledException>(() => scanner.ScanAsync(CreateOptions(temp.Path), null, cancellation.Token));
    }

    [LinkFact]
    public async Task ScanAsync_DoesNotFollowDirectoryReparsePointByDefault()
    {
        using var temp = TemporaryDirectory.Create();
        var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target"));
        await File.WriteAllBytesAsync(Path.Combine(target.FullName, "large.bin"), new byte[50]);
        var linkPath = Path.Combine(temp.Path, "link");

        Directory.CreateSymbolicLink(linkPath, target.FullName);

        var scanner = new FileSystemDiskScanner();

        var result = await scanner.ScanAsync(CreateOptions(temp.Path), null, CancellationToken.None);

        Assert.Equal(50, result.LogicalSizeBytes);
        Assert.Contains(result.Children, child => child.Name == "link" && child.IsReparsePoint && child.LogicalSizeBytes == 0);
    }

    private static ScanOptions CreateOptions(string rootPath)
    {
        return new ScanOptions
        {
            RootPath = rootPath,
            IncludeHiddenItems = true,
            IncludeSystemItems = true,
            FollowReparsePoints = false,
            CalculateAllocatedSize = false
        };
    }

    [Fact]
    public async Task ScanAsync_DoesNotFollowJunctionOutsideRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var target = TemporaryDirectory.Create();
        using var root = TemporaryDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(target.Path, "external.bin"), new byte[64]);
        var junction = Path.Combine(root.Path, "junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", junction, target.Path }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = await new FileSystemDiskScanner().ScanAsync(CreateOptions(root.Path), null, default);
            Assert.Equal(0, result.LogicalSizeBytes);
            Assert.True(Assert.Single(result.Children).IsReparsePoint);
            Assert.Empty(result.Children[0].Children);
            Assert.True(File.Exists(Path.Combine(target.Path, "external.bin")));
        }
        finally
        {
            // Remove the generated junction itself before recursively cleaning its containing fixture.
            Directory.Delete(junction, recursive: false);
        }
    }
}
