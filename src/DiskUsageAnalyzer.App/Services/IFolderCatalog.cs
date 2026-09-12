using System.IO;
using DiskUsageAnalyzer.Core.Formatting;

namespace DiskUsageAnalyzer.App.Services;

public sealed record FolderEntry(string Name, string FullPath, string Details, bool IsAccessible = true);

public interface IFolderCatalog
{
    Task<IReadOnlyList<FolderEntry>> GetRootsAsync(CancellationToken token);
    Task<IReadOnlyList<FolderEntry>> GetChildrenAsync(string path, CancellationToken token);
}

public sealed class FolderCatalog : IFolderCatalog
{
    public Task<IReadOnlyList<FolderEntry>> GetRootsAsync(CancellationToken token) => Task.Run<IReadOnlyList<FolderEntry>>(() =>
    {
        var entries = new List<FolderEntry>();
        foreach (var drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                entries.Add(drive.IsReady
                    ? new FolderEntry(string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name})",
                        drive.Name, $"{SizeFormatter.Format(drive.AvailableFreeSpace)} free of {SizeFormatter.Format(drive.TotalSize)}")
                    : new FolderEntry(drive.Name, drive.Name, "Unavailable", false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { entries.Add(new FolderEntry(drive.Name, drive.Name, ex.Message, false)); }
        }
        return entries;
    }, token);

    public Task<IReadOnlyList<FolderEntry>> GetChildrenAsync(string path, CancellationToken token) => Task.Run<IReadOnlyList<FolderEntry>>(() =>
    {
        var entries = new List<FolderEntry>();
        foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();
            if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                entries.Add(new FolderEntry(directory.Name, directory.FullName, directory.FullName));
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }, token);
}
