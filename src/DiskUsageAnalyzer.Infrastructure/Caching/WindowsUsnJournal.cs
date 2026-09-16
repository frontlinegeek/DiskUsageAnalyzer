using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DiskUsageAnalyzer.Core.Caching;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Updating;

namespace DiskUsageAnalyzer.Infrastructure.Caching;

public sealed class WindowsUsnJournal : IFileSystemJournal
{
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint ReasonData = 0x00000001 | 0x00000002 | 0x00000004 | 0x00008000;
    private const uint ReasonCreate = 0x00000100;
    private const uint ReasonDelete = 0x00000200;
    private const uint ReasonRenameOld = 0x00001000;
    private const uint ReasonRenameNew = 0x00002000;
    private const uint ReasonBasicInfo = 0x00008000;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;

    public Task<(VolumeIdentity? Volume, JournalCheckpoint? Journal)> CaptureAsync(string rootPath, CancellationToken cancellationToken)
        => Task.Run(() => Capture(rootPath, cancellationToken), cancellationToken);

    public Task<IncrementalChangeSet> ReadAsync(CachedScan scan, CancellationToken cancellationToken)
        => Task.Run(() => Read(scan, cancellationToken), cancellationToken);

    private static (VolumeIdentity?, JournalCheckpoint?) Capture(string rootPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return (null, null);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(rootPath));
        var fileSystem = new System.Text.StringBuilder(64);
        if (string.IsNullOrEmpty(volumeRoot) || !GetVolumeInformation(volumeRoot, null, 0, out var serial, out _, out _,
                fileSystem, fileSystem.Capacity)) return (null, null);
        var identity = new VolumeIdentity(volumeRoot, serial, fileSystem.ToString());
        if (!string.Equals(identity.FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase)) return (identity, null);
        using var handle = OpenVolume(volumeRoot);
        if (handle.IsInvalid || !TryQuery(handle, out var journal)) return (identity, null);
        return (identity, new JournalCheckpoint(journal.UsnJournalId, journal.NextUsn, journal.FirstUsn));
    }

    private static IncrementalChangeSet Read(CachedScan scan, CancellationToken token)
    {
        if (scan.Metadata.Volume is not { } expectedVolume || scan.Metadata.Journal is not { } previous)
            return new(IncrementalReadStatus.Unsupported, null, [], "The cached scan has no NTFS journal checkpoint.");
        var (actualVolume, currentCheckpoint) = Capture(scan.Metadata.RootPath, token);
        if (actualVolume is null || currentCheckpoint is null)
            return new(IncrementalReadStatus.Unsupported, null, [], "The USN journal is unavailable.");
        if (actualVolume.SerialNumber != expectedVolume.SerialNumber
            || !string.Equals(actualVolume.VolumeRoot, expectedVolume.VolumeRoot, StringComparison.OrdinalIgnoreCase))
            return new(IncrementalReadStatus.JournalChanged, currentCheckpoint, [], "The volume identity changed.");
        if (currentCheckpoint.JournalId != previous.JournalId)
            return new(IncrementalReadStatus.JournalChanged, currentCheckpoint, [], "The USN journal identity changed.");
        if (previous.NextUsn < currentCheckpoint.FirstUsn)
            return new(IncrementalReadStatus.JournalWrapped, currentCheckpoint, [], "The USN journal wrapped past the cache checkpoint.");
        if (previous.NextUsn >= currentCheckpoint.NextUsn)
            return new(IncrementalReadStatus.Available, currentCheckpoint, []);

        var idPaths = DiskTree.Enumerate(scan.Root).Where(i => i.FileId.HasValue)
            .GroupBy(i => i.FileId!.Value).ToDictionary(g => g.Key, g => g.First().FullPath);
        var oldNames = new Dictionary<ulong, string>();
        var changes = new List<DiskUsageChange>();
        using var handle = OpenVolume(actualVolume.VolumeRoot);
        if (handle.IsInvalid) return new(IncrementalReadStatus.Unsupported, null, [], new Win32Exception().Message);
        var start = previous.NextUsn;
        var target = currentCheckpoint.NextUsn;
        var buffer = Marshal.AllocHGlobal(1024 * 1024);
        try
        {
            while (start < target)
            {
                token.ThrowIfCancellationRequested();
                var input = new ReadUsnJournalData { StartUsn = start, ReasonMask = uint.MaxValue,
                    ReturnOnlyOnClose = 0, Timeout = 0, BytesToWaitFor = 0, UsnJournalId = previous.JournalId };
                if (!DeviceIoControl(handle, FsctlReadUsnJournal, ref input, Marshal.SizeOf<ReadUsnJournalData>(),
                        buffer, 1024 * 1024, out var returned, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error is ErrorJournalDeleteInProgress or ErrorJournalNotActive
                        ? new(IncrementalReadStatus.JournalChanged, currentCheckpoint, [], new Win32Exception(error).Message)
                        : error == ErrorJournalEntryDeleted
                            ? new(IncrementalReadStatus.JournalWrapped, currentCheckpoint, [], new Win32Exception(error).Message)
                            : new(IncrementalReadStatus.Unsafe, currentCheckpoint, [], new Win32Exception(error).Message);
                }
                if (returned < sizeof(long)) return new(IncrementalReadStatus.Unsafe, currentCheckpoint, [], "The journal returned an invalid buffer.");
                var next = Marshal.ReadInt64(buffer);
                var offset = sizeof(long);
                while (offset + 60 <= returned)
                {
                    token.ThrowIfCancellationRequested();
                    var record = Marshal.PtrToStructure<UsnRecord>(buffer + offset);
                    if (record.RecordLength < 60 || offset + record.RecordLength > returned)
                        return new(IncrementalReadStatus.Unsafe, currentCheckpoint, [], "The journal returned an invalid record.");
                    var name = Marshal.PtrToStringUni(buffer + offset + record.FileNameOffset, record.FileNameLength / 2) ?? "";
                    if (!ApplyRecord(scan.Metadata.RootPath, record, name, idPaths, oldNames, changes))
                        return new(IncrementalReadStatus.Unsafe, currentCheckpoint, [], "A changed entry could not be resolved safely from its NTFS file IDs.");
                    offset += checked((int)record.RecordLength);
                }
                if (next <= start) break;
                start = next;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return new(IncrementalReadStatus.Available,
            new JournalCheckpoint(currentCheckpoint.JournalId, Math.Max(start, target), currentCheckpoint.FirstUsn), Coalesce(changes));
    }

    private static bool ApplyRecord(string root, UsnRecord record, string name, Dictionary<ulong, string> paths,
        Dictionary<ulong, string> oldNames, List<DiskUsageChange> changes)
    {
        string? ParentPath() => paths.GetValueOrDefault(record.ParentFileReferenceNumber);
        string? Composed() => ParentPath() is { } parent ? Path.Combine(parent, name) : null;
        var known = paths.GetValueOrDefault(record.FileReferenceNumber);
        var path = known ?? Composed();
        if (path is null) return true; // Changes outside the cached root commonly have unknown parents.
        if (!DiskTree.ContainsPath(root, path)) return true;
        if ((record.Reason & ReasonRenameOld) != 0)
        {
            oldNames[record.FileReferenceNumber] = path;
            return true;
        }
        if ((record.Reason & ReasonRenameNew) != 0)
        {
            var newPath = Composed();
            if (newPath is null) return false;
            if (oldNames.Remove(record.FileReferenceNumber, out var oldPath))
                changes.Add(new DiskUsageChange { Kind = DiskUsageChangeKind.Renamed, OldFullPath = oldPath, FullPath = newPath });
            else changes.Add(new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = newPath });
            paths[record.FileReferenceNumber] = newPath;
            return true;
        }
        if ((record.Reason & ReasonDelete) != 0)
        {
            changes.Add(new DiskUsageChange { Kind = DiskUsageChangeKind.Deleted, FullPath = path });
            paths.Remove(record.FileReferenceNumber);
        }
        else if ((record.Reason & ReasonCreate) != 0)
        {
            changes.Add(new DiskUsageChange { Kind = DiskUsageChangeKind.Created, FullPath = path });
            paths[record.FileReferenceNumber] = path;
        }
        else if ((record.Reason & (ReasonData | ReasonBasicInfo)) != 0)
            changes.Add(new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = path });
        return true;
    }

    private static IReadOnlyList<DiskUsageChange> Coalesce(List<DiskUsageChange> changes)
    {
        var result = new List<DiskUsageChange>();
        var last = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change.Kind == DiskUsageChangeKind.Renamed) { result.Add(change); continue; }
            if (last.TryGetValue(change.FullPath, out var index)) result[index] = change;
            else { last[change.FullPath] = result.Count; result.Add(change); }
        }
        return result;
    }

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        var device = @"\\.\" + volumeRoot.TrimEnd(Path.DirectorySeparatorChar);
        return CreateFile(device, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }

    private static bool TryQuery(SafeFileHandle handle, out UsnJournalData data)
    {
        var output = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalData>());
        try
        {
            if (!DeviceIoControl(handle, FsctlQueryUsnJournal, IntPtr.Zero, 0, output,
                    Marshal.SizeOf<UsnJournalData>(), out _, IntPtr.Zero)) { data = default; return false; }
            data = Marshal.PtrToStructure<UsnJournalData>(output); return true;
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalData { public ulong UsnJournalId; public long FirstUsn, NextUsn, LowestValidUsn, MaxUsn; public ulong MaximumSize, AllocationDelta; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData { public long StartUsn; public uint ReasonMask, ReturnOnlyOnClose; public ulong Timeout, BytesToWaitFor, UsnJournalId; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsnRecord
    {
        public uint RecordLength; public ushort MajorVersion, MinorVersion; public ulong FileReferenceNumber, ParentFileReferenceNumber;
        public long Usn, TimeStamp; public uint Reason, SourceInfo, SecurityId, FileAttributes; public ushort FileNameLength, FileNameOffset;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, System.Text.StringBuilder? volumeNameBuffer,
        int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        System.Text.StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, ref ReadUsnJournalData inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);
}
