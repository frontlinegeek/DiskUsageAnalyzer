namespace DiskUsageAnalyzer.Core.Updating;

public sealed class DiskUsageChange
{
    public required DiskUsageChangeKind Kind { get; init; }

    public required string FullPath { get; init; }

    public string? OldFullPath { get; init; }
}
