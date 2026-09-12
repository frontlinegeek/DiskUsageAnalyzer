using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Core.Filtering;

public sealed class DiskItemFilter
{
    private string? _extension;
    private string? _normalizedExtension;

    public string? SearchText { get; init; }

    public long MinimumSizeBytes { get; init; }

    public string? Extension
    {
        get => _extension;
        init
        {
            _extension = value;
            _normalizedExtension = NormalizeExtension(value);
        }
    }

    public bool Matches(DiskItem item)
    {
        if (item.LogicalSizeBytes < MinimumSizeBytes)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText)
            && !item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            && !item.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_normalizedExtension is not null)
        {
            if (item.ItemType != DiskItemType.File
                || !string.Equals(Path.GetExtension(item.FullPath), _normalizedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        extension = extension.Trim();
        return extension[0] == '.' ? extension : $".{extension}";
    }
}
