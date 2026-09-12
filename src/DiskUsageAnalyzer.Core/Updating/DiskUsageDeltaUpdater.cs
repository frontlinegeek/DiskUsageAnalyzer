using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Core.Updating;

public sealed class DiskUsageDeltaUpdater
{
    public DiskUsageDeltaUpdateResult Apply(DiskItem root, IEnumerable<DiskUsageChange> changes,
        IDiskItemSnapshotProvider snapshotProvider, CancellationToken cancellationToken = default)
    {
        var result = new DiskUsageDeltaUpdateResult();
        var index = new Dictionary<string, DiskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in DiskTree.Enumerate(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index.Add(item.FullPath, item);
        }
        var dirty = new HashSet<DiskItem>();

        DiskItem? Parent(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } parent
            ? index.GetValueOrDefault(parent) : null;
        void Mark(DiskItem? item)
        {
            while (item is not null && dirty.Add(item)) item = Parent(item.FullPath);
        }
        void Rescan(string path)
        {
            var item = index.GetValueOrDefault(path) ?? Parent(path);
            while (item is null && Path.GetDirectoryName(path) is { } parent && parent != path)
            {
                path = parent;
                item = index.GetValueOrDefault(path);
            }
            result.RescanPaths.Add(item?.ItemType == DiskItemType.File
                ? Parent(item.FullPath)?.FullPath ?? root.FullPath : item?.FullPath ?? root.FullPath);
        }
        void Remove(string path)
        {
            if (!index.TryGetValue(path, out var item)) return;
            var parent = Parent(path);
            if (parent is null) { result.RescanPaths.Add(root.FullPath); return; }
            parent.Children.Remove(item);
            foreach (var descendant in DiskTree.Enumerate(item)) { index.Remove(descendant.FullPath); dirty.Remove(descendant); }
            Mark(parent);
        }
        void Upsert(string path)
        {
            var snapshot = snapshotProvider.ReadSnapshot(path);
            var existing = index.GetValueOrDefault(path);
            if (snapshot.Status == SnapshotStatus.Inaccessible)
            {
                if (existing is not null && snapshot.Error is not null)
                {
                    existing.Errors.RemoveAll(error => string.Equals(error.Path, path, StringComparison.OrdinalIgnoreCase));
                    existing.Errors.Add(snapshot.Error);
                    Mark(existing);
                }
                Rescan(path);
                return;
            }
            if (snapshot.Status is SnapshotStatus.Missing or SnapshotStatus.Excluded) { Remove(path); return; }
            var item = snapshot.Item!;
            var parent = Parent(path);
            if (parent is null || parent.ItemType == DiskItemType.File || parent.IsReparsePoint)
            { Rescan(path); return; }
            if (item.ItemType != DiskItemType.File || existing is { ItemType: not DiskItemType.File })
            { Rescan(path); return; }
            if (existing is not null) parent.Children.Remove(existing);
            parent.Children.Add(item);
            index[path] = item;
            Mark(parent);
        }

        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DiskTree.ContainsPath(root.FullPath, change.FullPath))
                {
                    if (change.OldFullPath is { } old && DiskTree.ContainsPath(root.FullPath, old)) Remove(old);
                    continue;
                }
                if (change.Kind == DiskUsageChangeKind.Overflow)
                { result.RescanPaths.Add(root.FullPath); continue; }
                switch (change.Kind)
                {
                    case DiskUsageChangeKind.Deleted:
                        Remove(change.FullPath);
                        break;
                    case DiskUsageChangeKind.Renamed when change.OldFullPath is { } oldPath && index.TryGetValue(oldPath, out var item):
                        var renamedSnapshot = snapshotProvider.ReadSnapshot(change.FullPath);
                        if (renamedSnapshot.Status is SnapshotStatus.Excluded or SnapshotStatus.Missing)
                        { Remove(oldPath); break; }
                        if (renamedSnapshot.Status == SnapshotStatus.Inaccessible)
                        { Rescan(oldPath); Rescan(change.FullPath); break; }
                        var newParent = Parent(change.FullPath);
                        var oldParent = Parent(oldPath);
                        if (newParent is null || oldParent is null || newParent.IsReparsePoint
                            || newParent.ItemType == DiskItemType.File || DiskTree.ContainsPath(oldPath, newParent.FullPath))
                        { Rescan(oldPath); Rescan(change.FullPath); break; }
                        if (index.TryGetValue(change.FullPath, out var collision) && !ReferenceEquals(collision, item))
                            Remove(change.FullPath);
                        oldParent.Children.Remove(item);
                        var descendants = DiskTree.Enumerate(item).ToArray();
                        foreach (var child in descendants) index.Remove(child.FullPath);
                        foreach (var child in descendants)
                        {
                            var relative = Path.GetRelativePath(oldPath, child.FullPath);
                            child.FullPath = relative == "." ? change.FullPath : Path.Combine(change.FullPath, relative);
                            child.Name = Path.GetFileName(child.FullPath);
                            for (var i = 0; i < child.Errors.Count; i++)
                            {
                                var error = child.Errors[i];
                                if (DiskTree.ContainsPath(oldPath, error.Path))
                                    child.Errors[i] = new ScanError
                                    {
                                        Path = Path.Combine(change.FullPath, Path.GetRelativePath(oldPath, error.Path)),
                                        Message = error.Message,
                                        ExceptionType = error.ExceptionType
                                    };
                            }
                            index[child.FullPath] = child;
                        }
                        newParent.Children.Add(item);
                        Mark(oldParent);
                        Mark(newParent);
                        // Reconcile metadata/options after preserving the known subtree and its totals.
                        if (item.ItemType == DiskItemType.File) Upsert(item.FullPath);
                        else Rescan(item.FullPath);
                        break;
                    default:
                        Upsert(change.FullPath);
                        break;
                }
                result.AppliedChanges++;
            }
        }
        finally
        {
            foreach (var item in dirty.OrderByDescending(item => item.FullPath.Length)) DiskTree.Recalculate(item);
        }
        return result;
    }
}
