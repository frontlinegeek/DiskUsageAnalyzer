using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Infrastructure.Scanning;

namespace DiskUsageAnalyzer.Infrastructure.Updating;

public sealed class FileSystemDiskItemSnapshotProvider : IDiskItemSnapshotProvider
{
    private readonly IFileSystemMetadata _metadata;
    private readonly ScanOptions _options;

    public FileSystemDiskItemSnapshotProvider(ScanOptions? options = null, IFileSystemMetadata? metadata = null)
    {
        _options = options ?? new ScanOptions { RootPath = ".", IncludeHiddenItems = true };
        _metadata = metadata ?? new FileSystemMetadata();
    }

    public bool TryGetSnapshot(string fullPath, out DiskItem item)
    {
        var result = ReadSnapshot(fullPath);
        item = result.Item!;
        return result.Status == SnapshotStatus.Available;
    }

    public SnapshotResult ReadSnapshot(string fullPath)
    {
        try
        {
            var entry = _metadata.GetEntry(fullPath);
            if (entry.Error is not null) return new(SnapshotStatus.Inaccessible, Error: entry.Error);
            if ((!_options.IncludeHiddenItems && entry.Attributes.HasFlag(FileAttributes.Hidden))
                || (!_options.IncludeSystemItems && entry.Attributes.HasFlag(FileAttributes.System)))
                return new(SnapshotStatus.Excluded);
            return new(SnapshotStatus.Available, new DiskItem { Name = Path.GetFileName(fullPath), FullPath = fullPath,
                ItemType = entry.Attributes.HasFlag(FileAttributes.Directory) ? DiskItemType.Directory : DiskItemType.File,
                LogicalSizeBytes = entry.Length, LastModified = entry.LastModified,
                AllocatedSizeBytes = _options.CalculateAllocatedSize ? entry.AllocatedLength : null,
                IsReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint), Attributes = entry.Attributes,
                FileId = entry.FileId, ParentFileId = entry.ParentFileId });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(SnapshotStatus.Missing); }
        catch (Exception ex) when (FileSystemMetadata.IsRecoverable(ex))
        { return new(SnapshotStatus.Inaccessible, Error: FileSystemMetadata.Error(fullPath, ex)); }
    }
}
