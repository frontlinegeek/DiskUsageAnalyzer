namespace DiskUsageAnalyzer.Core.Scanning;

public sealed class ScanProgress
{
    public string? CurrentPath { get; init; }

    public long FilesScanned { get; init; }

    public long FoldersScanned { get; init; }

    public long BytesScanned { get; init; }

    public long ErrorsEncountered { get; init; }
}
