namespace DiskUsageAnalyzer.Core.Updating;

public sealed class ChangeAccumulator(string rootPath)
{
    private readonly List<DiskUsageChange> _changes = [];
    private DateTimeOffset _first;
    private DateTimeOffset _last;
    private bool _overflow;
    public int Count => _changes.Count;

    public void Add(DiskUsageChange change, DateTimeOffset now)
    {
        if (_changes.Count == 0) _first = now;
        _last = now;
        if (_overflow) return;
        if (change.Kind == DiskUsageChangeKind.Overflow || _changes.Count >= 10000)
        {
            _changes.Clear();
            _changes.Add(new DiskUsageChange { Kind = DiskUsageChangeKind.Overflow, FullPath = rootPath });
            _overflow = true;
        }
        else _changes.Add(change);
    }

    public TimeSpan DueIn(DateTimeOffset now)
    {
        var due = (_last + TimeSpan.FromMilliseconds(900)) < (_first + TimeSpan.FromSeconds(2))
            ? _last + TimeSpan.FromMilliseconds(900) : _first + TimeSpan.FromSeconds(2);
        return due > now ? due - now : TimeSpan.Zero;
    }

    public IReadOnlyCollection<DiskUsageChange> Drain()
    {
        var changes = _changes.ToArray();
        _changes.Clear();
        _overflow = false;
        return changes;
    }
}
