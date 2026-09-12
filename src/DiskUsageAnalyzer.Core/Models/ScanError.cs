namespace DiskUsageAnalyzer.Core.Models;

public sealed class ScanError
{
    public required string Path { get; init; }

    public required string Message { get; init; }

    public string? ExceptionType { get; init; }
}
