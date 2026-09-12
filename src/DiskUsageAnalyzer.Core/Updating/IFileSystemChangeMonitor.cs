namespace DiskUsageAnalyzer.Core.Updating;

public sealed record ChangeBatch(long Session, IReadOnlyCollection<DiskUsageChange> Changes);

public interface IFileSystemChangeMonitor : IDisposable
{
    event Action<ChangeBatch>? ChangesReady;
    bool IsWatching { get; }
    void Start(string rootPath, long session);
    void Stop();
}
