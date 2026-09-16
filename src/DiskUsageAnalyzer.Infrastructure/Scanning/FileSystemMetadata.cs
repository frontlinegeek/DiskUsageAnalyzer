using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

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
            var (allocated, fileId) = WindowsMetadata.TryRead(entry.FullName);
            return new FileSystemEntry(entry.FullName, attributes,
                entry is FileInfo file && (attributes & FileAttributes.ReparsePoint) == 0 ? file.Length : 0,
                entry.LastWriteTimeUtc, AllocatedLength: allocated, FileId: fileId);
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

internal static class WindowsMetadata
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 1 | 2 | 4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    public static (long? Allocated, ulong? FileId) TryRead(string path)
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        long? allocated = null;
        Marshal.SetLastPInvokeError(0);
        var low = GetCompressedFileSize(path, out var high);
        if (low != uint.MaxValue || Marshal.GetLastPInvokeError() == 0)
            allocated = checked((long)(((ulong)high << 32) | low));
        using var handle = CreateFile(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return (allocated, null);
        return (allocated, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
