using System.Collections.ObjectModel;
using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.Core.Formatting;

namespace DiskUsageAnalyzer.App.ViewModels;

public sealed class DiskItemViewModel : ViewModelBase
{
    private readonly ResultSnapshot _snapshot;
    private readonly IReadOnlySet<string> _visible;
    private readonly IReadOnlySet<string> _expandedPaths;
    private readonly string? _selectedPath;
    private ObservableCollection<DiskItemViewModel>? _children;
    private bool _isExpanded;
    private bool _isSelected;
    private readonly int _depth;
    public DiskItemViewModel(ResultRow row, long parentSize, ResultSnapshot snapshot, IReadOnlySet<string> visible,
        IReadOnlySet<string>? expandedPaths = null, string? selectedPath = null, int depth = 0)
    {
        Item = row; ParentSizeBytes = parentSize; _snapshot = snapshot; _visible = visible;
        _depth = depth;
        _expandedPaths = expandedPaths ?? new HashSet<string>(); _selectedPath = selectedPath;
        _isExpanded = _expandedPaths.Contains(row.FullPath) || (expandedPaths is null && row.FullPath == snapshot.RootPath);
        _isSelected = string.Equals(row.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase);
    }
    public ResultRow Item { get; }
    public IEnumerable<DiskItemViewModel> LoadedChildren => _children is null ? [] : _children;
    public ObservableCollection<DiskItemViewModel> Children => _children ??= new(Item.Children
        .Where(_visible.Contains).Select(path => new DiskItemViewModel(_snapshot.Rows[path], Item.SizeBytes,
            _snapshot, _visible, _expandedPaths, _selectedPath, _depth + 1)));
    public System.Windows.Thickness RowMargin => new(-19 * _depth, 2, 0, 2);
    public System.Windows.Thickness NameMargin => new(19 * _depth, 0, 0, 0);
    public string Name => Item.Name;
    public string FullPath => Item.FullPath;
    public string Type => Item.ItemType.ToString();
    public string Size => SizeFormatter.Format(Item.SizeBytes);
    public string FileCount => Item.FileCount.ToString("N0");
    public string FolderCount => Item.FolderCount.ToString("N0");
    public string LastModified => Item.LastModified?.LocalDateTime.ToString("g") ?? "";
    public bool HasErrors => Item.ErrorCount > 0;
    public string ErrorCount => HasErrors ? Item.ErrorCount.ToString("N0") : "";
    public bool IsReparsePoint => Item.IsReparsePoint;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public long ParentSizeBytes { get; }
    public double PercentOfParent => ParentSizeBytes <= 0 ? 0 : Math.Clamp((double)Item.SizeBytes / ParentSizeBytes, 0, 1);
    public string PercentText => $"{PercentOfParent:P1}";
    public double BarWidth => PercentOfParent * 190;
}
