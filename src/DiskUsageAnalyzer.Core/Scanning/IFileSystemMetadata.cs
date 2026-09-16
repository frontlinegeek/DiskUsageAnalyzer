using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Core.Scanning;

public readonly record struct FileSystemEntry(string FullPath, FileAttributes Attributes, long Length,
    DateTimeOffset? LastModified, ScanError? Error = null, long? AllocatedLength = null,
    ulong? FileId = null, ulong? ParentFileId = null);

public interface IFileSystemMetadata
{
    FileSystemEntry GetEntry(string path);
    IEnumerable<FileSystemEntry> Enumerate(string directory);
}
