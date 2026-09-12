using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Core.Updating;

public interface IDiskItemSnapshotProvider
{
    bool TryGetSnapshot(string fullPath, out DiskItem item);

    SnapshotResult ReadSnapshot(string fullPath) => TryGetSnapshot(fullPath, out var item)
        ? new(SnapshotStatus.Available, item) : new(SnapshotStatus.Missing);
}
