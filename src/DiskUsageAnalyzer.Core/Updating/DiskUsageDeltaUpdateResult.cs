namespace DiskUsageAnalyzer.Core.Updating;

public sealed class DiskUsageDeltaUpdateResult
{
    public int AppliedChanges { get; set; }

    public HashSet<string> RescanPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RequiresRescan => RescanPaths.Count > 0;
}
