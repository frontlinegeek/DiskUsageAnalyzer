namespace DiskUsageAnalyzer.Core.Models;

public sealed class DiskItem
{
    public required string Name { get; set; }

    public required string FullPath { get; set; }

    public required DiskItemType ItemType { get; init; }

    public long LogicalSizeBytes { get; set; }

    public long? AllocatedSizeBytes { get; set; }

    public int FileCount { get; set; }

    public int FolderCount { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public bool IsReparsePoint { get; set; }

    public long SubtreeErrorCount { get; set; }

    public List<DiskItem> Children { get; } = [];

    public List<ScanError> Errors { get; } = [];
}
