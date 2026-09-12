using DiskUsageAnalyzer.Core.Updating;

namespace DiskUsageAnalyzer.Infrastructure.Watching;

public interface IFileSystemEventSource : IDisposable
{
    event Action<DiskUsageChange>? Changed;
    void Start();
}

public sealed class FileSystemEventSource : IFileSystemEventSource
{
    private readonly FileSystemWatcher _watcher;
    public event Action<DiskUsageChange>? Changed;

    public FileSystemEventSource(string root)
    {
        _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024 };
        _watcher.Created += (_, e) => Emit(DiskUsageChangeKind.Created, e.FullPath);
        _watcher.Changed += (_, e) => Emit(DiskUsageChangeKind.Changed, e.FullPath);
        _watcher.Deleted += (_, e) => Emit(DiskUsageChangeKind.Deleted, e.FullPath);
        _watcher.Renamed += (_, e) => Changed?.Invoke(new DiskUsageChange
            { Kind = DiskUsageChangeKind.Renamed, FullPath = e.FullPath, OldFullPath = e.OldFullPath });
        _watcher.Error += (_, _) => Emit(DiskUsageChangeKind.Overflow, root);
    }

    private void Emit(DiskUsageChangeKind kind, string path) => Changed?.Invoke(new DiskUsageChange { Kind = kind, FullPath = path });
    public void Start() => _watcher.EnableRaisingEvents = true;
    public void Dispose() => _watcher.Dispose();
}
