using System.Diagnostics;
using System.IO;
using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.App.Services;

public interface IFileActions
{
    void OpenFolder(string path);
    void CopyPath(string path);
    Task DeleteAsync(string root, string path, DiskItemType type, CancellationToken token);
}

public sealed class FileActions : IFileActions
{
    public void OpenFolder(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
    public void CopyPath(string path) => System.Windows.Clipboard.SetText(path);

    public Task DeleteAsync(string root, string path, DiskItemType type, CancellationToken token) => Task.Run(() =>
    {
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        if (!DiskTree.ContainsPath(root, path) || string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deletion must stay below the scanned root.");
        var current = path;
        while (current is not null)
        {
            token.ThrowIfCancellationRequested();
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Deletion through reparse points is disabled.");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
        var isDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        if (isDirectory != (type == DiskItemType.Directory)) throw new IOException("The selected item changed type.");
        if (isDirectory) Directory.Delete(path, true); else File.Delete(path);
    }, token);
}
