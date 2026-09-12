using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.App.Services;

public sealed record ResultRow(string Name, string FullPath, DiskItemType ItemType, long SizeBytes,
    int FileCount, int FolderCount, DateTimeOffset? LastModified, bool IsReparsePoint, long ErrorCount, string[] Children);

public sealed record ResultSnapshot(string RootPath, IReadOnlyDictionary<string, ResultRow> Rows)
{
    public ResultRow[] FilesUnder(string? folderPath, IReadOnlySet<string> visible, bool smallestFirst, CancellationToken token)
    {
        if (folderPath is null || !Rows.TryGetValue(System.IO.Path.TrimEndingDirectorySeparator(folderPath), out var folder)
            || folder.ItemType == DiskItemType.File) return [];
        var files = new List<ResultRow>();
        var pending = new Stack<string>(folder.Children);
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            var row = Rows[path];
            if (row.ItemType == DiskItemType.File)
            {
                if (visible.Contains(path)) files.Add(row);
            }
            else foreach (var child in row.Children) pending.Push(child);
        }
        var ordered = smallestFirst ? files.OrderBy(row => row.SizeBytes) : files.OrderByDescending(row => row.SizeBytes);
        return ordered.ThenBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ResultSnapshot Create(DiskItem root) => new(root.FullPath,
        DiskTree.Enumerate(root).ToDictionary(item => item.FullPath, item => new ResultRow(item.Name,
            item.FullPath, item.ItemType, item.LogicalSizeBytes, item.FileCount, item.FolderCount,
            item.LastModified, item.IsReparsePoint, item.SubtreeErrorCount,
            item.Children.Select(child => child.FullPath).ToArray()), StringComparer.OrdinalIgnoreCase));

    public HashSet<string> Filter(string? search, string? extension, long minimum, CancellationToken token)
    {
        search = search?.Trim();
        extension = extension?.Trim().TrimStart('.');
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RootPath };
        foreach (var row in Rows.Values)
        {
            token.ThrowIfCancellationRequested();
            if (row.SizeBytes < minimum
                || (!string.IsNullOrEmpty(search) && !row.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(extension) && (row.ItemType != DiskItemType.File
                    || !string.Equals(System.IO.Path.GetExtension(row.FullPath), "." + extension, StringComparison.OrdinalIgnoreCase)))) continue;
            var path = row.FullPath;
            while (visible.Add(path) && System.IO.Path.GetDirectoryName(path) is { } parent && Rows.ContainsKey(parent)) path = parent;
        }
        return visible;
    }
}
