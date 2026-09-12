namespace DiskUsageAnalyzer.Core.Scanning;

public sealed class ScanOptions
{
    public required string RootPath { get; init; }

    public bool IncludeHiddenItems { get; init; }

    public bool IncludeSystemItems { get; init; }

    public bool FollowReparsePoints { get; init; }

    public bool CalculateAllocatedSize { get; init; }
}
