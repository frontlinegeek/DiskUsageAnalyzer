using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;

namespace DiskUsageAnalyzer.Core.Caching;

public sealed record VolumeIdentity(string VolumeRoot, uint SerialNumber, string FileSystemName);

public sealed record JournalCheckpoint(ulong JournalId, long NextUsn, long FirstUsn);

public sealed record CachedScanMetadata(long CacheId, string RootPath, DateTimeOffset CompletedAt,
    ScanOptions Options, VolumeIdentity? Volume, JournalCheckpoint? Journal);

public sealed record CachedScan(CachedScanMetadata Metadata, DiskItem Root);

public sealed record CachedEntry(long EntryId, long? ParentEntryId, DiskItem Item);

public interface IScanCache
{
    Task<CachedScanMetadata?> FindLatestAsync(string rootPath, CancellationToken cancellationToken);
    Task<CachedScan?> LoadLatestAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
    IAsyncEnumerable<CachedEntry> ReadChildrenAsync(long cacheId, long? parentEntryId, CancellationToken cancellationToken);
    Task ReplaceAsync(DiskItem root, ScanOptions options, VolumeIdentity? volume, JournalCheckpoint? journal,
        DateTimeOffset completedAt, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
    Task UpdateAsync(CachedScanMetadata metadata, DiskItem root, JournalCheckpoint? journal,
        DateTimeOffset completedAt, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
    Task DeleteAsync(string rootPath, CancellationToken cancellationToken);
}

public enum IncrementalReadStatus { Available, Unsupported, JournalChanged, JournalWrapped, Unsafe }

public sealed record IncrementalChangeSet(IncrementalReadStatus Status, JournalCheckpoint? Checkpoint,
    IReadOnlyList<DiskUsageChange> Changes, string? Reason = null);

public interface IFileSystemJournal
{
    Task<(VolumeIdentity? Volume, JournalCheckpoint? Journal)> CaptureAsync(string rootPath, CancellationToken cancellationToken);
    Task<IncrementalChangeSet> ReadAsync(CachedScan scan, CancellationToken cancellationToken);
}

public enum CacheRefreshKind { CacheLoaded, Incremental, FullScan }

public sealed record CacheRefreshResult(CacheRefreshKind Kind, DateTimeOffset UpdatedAt, string? Detail = null);
