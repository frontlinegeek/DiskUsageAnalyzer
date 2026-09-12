using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;

namespace DiskUsageAnalyzer.Infrastructure.Scanning;

public sealed class FileSystemMetadata : IFileSystemMetadata
{
    public FileSystemEntry GetEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        return Read((attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path));
    }

    public IEnumerable<FileSystemEntry> Enumerate(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            yield return Read(entry);
    }

    private static FileSystemEntry Read(FileSystemInfo entry)
    {
        var attributes = entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
        try
        {
            attributes = entry.Attributes;
            return new FileSystemEntry(entry.FullName, attributes,
                entry is FileInfo file && (attributes & FileAttributes.ReparsePoint) == 0 ? file.Length : 0,
                entry.LastWriteTimeUtc);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new FileSystemEntry(entry.FullName, attributes, 0, null, Error(entry.FullName, ex));
        }
    }

    public static bool IsRecoverable(Exception exception) => exception is IOException or UnauthorizedAccessException;

    public static ScanError Error(string path, Exception exception) => new()
    {
        Path = path, Message = exception.Message, ExceptionType = exception.GetType().Name
    };
}
