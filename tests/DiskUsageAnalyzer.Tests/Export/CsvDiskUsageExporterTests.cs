using System.Globalization;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Infrastructure.Export;

namespace DiskUsageAnalyzer.Tests.Export;

public sealed class CsvDiskUsageExporterTests
{
    [Fact]
    public async Task Export_EscapesValuesAndIncludesLocalErrorsAndInvariantNumbers()
    {
        using var temp = TemporaryDirectory.Create();
        var path = Path.Combine(temp.Path, "report.csv");
        var root = new DiskItem { Name = "a,\"b\"\n\u00e9", FullPath = temp.Path, ItemType = DiskItemType.Directory,
            LogicalSizeBytes = 1234, SubtreeErrorCount = 1 };
        root.Errors.Add(new ScanError { Path = temp.Path, Message = "Denied", ExceptionType = "UnauthorizedAccessException" });
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await new CsvDiskUsageExporter().ExportAsync(root, path, default);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"a,\"\"b\"\"\n\u00e9\"", text);
        Assert.Contains("\"1234\"", text);
        Assert.Contains("ErrorDetails", text);
        Assert.Contains("Denied", text);
        Assert.DoesNotContain(".tmp", string.Join(',', Directory.GetFiles(temp.Path).Select(Path.GetExtension)));
    }

    [Fact]
    public async Task PreCanceledExport_PreservesExistingDestination()
    {
        using var temp = TemporaryDirectory.Create();
        var path = Path.Combine(temp.Path, "report.csv");
        await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CsvDiskUsageExporter().ExportAsync(
            new DiskItem { Name = "root", FullPath = temp.Path, ItemType = DiskItemType.Directory }, path, cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task LockedDestination_PreservesExistingContentsAndCleansTemporaryFile()
    {
        using var temp = TemporaryDirectory.Create();
        var path = Path.Combine(temp.Path, "report.csv");
        await File.WriteAllTextAsync(path, "original");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => new CsvDiskUsageExporter().ExportAsync(
                new DiskItem { Name = "root", FullPath = temp.Path, ItemType = DiskItemType.Directory }, path, default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }
}
