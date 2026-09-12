using System.Diagnostics;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;

namespace DiskUsageAnalyzer.Infrastructure.Scanning;

public sealed class FileSystemDiskScanner : IDiskScanner
{
    private readonly IFileSystemMetadata _metadata;
    private readonly TimeProvider _time;

    public FileSystemDiskScanner(IFileSystemMetadata? metadata = null, TimeProvider? time = null)
    {
        _metadata = metadata ?? new FileSystemMetadata();
        _time = time ?? TimeProvider.System;
    }

    public Task<DiskItem> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        if (options.FollowReparsePoints || options.CalculateAllocatedSize)
            throw new NotSupportedException("Link traversal and allocated sizes are not supported.");
        return Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken);
    }

    private DiskItem Scan(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        var root = new DiskItem
        {
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            FullPath = path,
            ItemType = string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)
                ? DiskItemType.Drive : DiskItemType.Directory
        };
        if (root.Name.Length == 0) root.Name = path;
        long files = 0, folders = 0, bytes = 0, errors = 0;
        var started = _time.GetTimestamp();
        var lastReport = started;
        void Report(string current, bool force = false)
        {
            if (progress is null) return;
            var now = _time.GetTimestamp();
            if (!force && _time.GetElapsedTime(lastReport, now) < TimeSpan.FromMilliseconds(100)) return;
            lastReport = now;
            progress?.Report(new ScanProgress
            {
                CurrentPath = current,
                FilesScanned = files,
                FoldersScanned = folders,
                BytesScanned = bytes,
                ErrorsEncountered = errors
            });
        }
        void Record(DiskItem item, ScanError error)
        {
            item.Errors.Add(error);
            item.SubtreeErrorCount++;
            errors++;
            Trace.TraceWarning("Scan error. Path={0}; Type={1}; Message={2}", error.Path, error.ExceptionType, error.Message);
        }

        Trace.TraceInformation("Scan started. RootPath={0}", path);
        Report(path, true);
        try
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entry = _metadata.GetEntry(path);
                if ((entry.Attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Scan root must be a directory.");
                root.LastModified = entry.LastModified;
                root.IsReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                if (entry.Error is not null) Record(root, entry.Error);
            }
            catch (Exception ex) when (FileSystemMetadata.IsRecoverable(ex))
            {
                Record(root, FileSystemMetadata.Error(path, ex));
                Report(path, true);
                return root;
            }

            var pending = new Stack<DiskItem>();
            var directories = new List<DiskItem>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                folders++;
                directories.Add(directory);
                Report(directory.FullPath);
                if (directory.IsReparsePoint || directory.Errors.Count > 0) continue;
                try
                {
                    // Close each enumerator before visiting children; aggregate in reverse visitation order.
                    foreach (var entry in _metadata.Enumerate(directory.FullPath))
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.Error is null && ((!options.IncludeHiddenItems && entry.Attributes.HasFlag(FileAttributes.Hidden))
                            || (!options.IncludeSystemItems && entry.Attributes.HasFlag(FileAttributes.System)))) continue;
                        var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                        var item = new DiskItem
                        {
                            Name = Path.GetFileName(entry.FullPath),
                            FullPath = entry.FullPath,
                            ItemType = isDirectory ? DiskItemType.Directory : DiskItemType.File,
                            LogicalSizeBytes = entry.Length,
                            LastModified = entry.LastModified,
                            IsReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        };
                        directory.Children.Add(item);
                        if (entry.Error is not null) Record(item, entry.Error);
                        if (isDirectory) pending.Push(item);
                        else { files++; bytes += item.LogicalSizeBytes; }
                        Report(entry.FullPath);
                    }
                }
                catch (Exception ex) when (FileSystemMetadata.IsRecoverable(ex))
                {
                    Record(directory, FileSystemMetadata.Error(directory.FullPath, ex));
                }
            }
            for (var i = directories.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                DiskTree.Recalculate(directories[i]);
            }
            Report(path, true);
            Trace.TraceInformation("Scan complete. RootPath={0}; Duration={1}; Files={2}; Folders={3}; Errors={4}; Bytes={5}",
                path, _time.GetElapsedTime(started), files, folders, errors, bytes);
            return root;
        }
        catch (OperationCanceledException) { Trace.TraceInformation("Scan canceled. RootPath={0}", path); throw; }
        catch (Exception ex) { Trace.TraceError("Scan failed. RootPath={0}; Exception={1}", path, ex); throw; }
    }
}
