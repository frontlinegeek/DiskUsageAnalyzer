using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Core.Scanning;

public interface IDiskScanner
{
    Task<DiskItem> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
