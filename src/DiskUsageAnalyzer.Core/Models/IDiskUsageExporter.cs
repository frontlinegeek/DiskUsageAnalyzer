namespace DiskUsageAnalyzer.Core.Models;

public interface IDiskUsageExporter
{
    Task ExportAsync(DiskItem root, string outputPath, CancellationToken cancellationToken);
}
