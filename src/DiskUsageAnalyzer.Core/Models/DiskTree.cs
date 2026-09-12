namespace DiskUsageAnalyzer.Core.Models;

public static class DiskTree
{
    public static bool ContainsPath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<DiskItem> Enumerate(DiskItem root)
    {
        var stack = new Stack<DiskItem>();
        stack.Push(root);
        while (stack.TryPop(out var item))
        {
            yield return item;
            for (var i = item.Children.Count - 1; i >= 0; i--) stack.Push(item.Children[i]);
        }
    }

    public static void Recalculate(DiskItem item)
    {
        item.SubtreeErrorCount = item.Errors.Count;
        if (item.ItemType == DiskItemType.File) return;
        item.LogicalSizeBytes = 0;
        item.FileCount = 0;
        item.FolderCount = 0;
        foreach (var child in item.Children)
        {
            item.LogicalSizeBytes += child.LogicalSizeBytes;
            item.FileCount += child.FileCount + (child.ItemType == DiskItemType.File ? 1 : 0);
            item.FolderCount += child.FolderCount + (child.ItemType != DiskItemType.File ? 1 : 0);
            item.SubtreeErrorCount += child.SubtreeErrorCount;
        }
        item.Children.Sort(static (a, b) =>
        {
            var size = b.LogicalSizeBytes.CompareTo(a.LogicalSizeBytes);
            return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
    }
}
