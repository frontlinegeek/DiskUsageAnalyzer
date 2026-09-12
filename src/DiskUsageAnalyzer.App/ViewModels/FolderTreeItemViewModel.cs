using System.Collections.ObjectModel;
using DiskUsageAnalyzer.App.Services;

namespace DiskUsageAnalyzer.App.ViewModels;

public sealed class FolderTreeItemViewModel : ViewModelBase
{
    private readonly IFolderCatalog _catalog;
    private readonly Action<string> _select;
    private readonly CancellationToken _token;
    private bool _loaded;
    private bool _expanded;
    private bool _selected;
    public FolderTreeItemViewModel(FolderEntry entry, IFolderCatalog catalog, Action<string> select, CancellationToken token)
    {
        Name = entry.Name; FullPath = entry.FullPath; Details = entry.Details; IsAccessible = entry.IsAccessible;
        _catalog = catalog; _select = select; _token = token;
        if (IsAccessible) Children.Add(new FolderTreeItemViewModel(new FolderEntry("Loading...", "", "", false), catalog, select, token));
    }
    public string Name { get; }
    public string FullPath { get; }
    public string Details { get; private set; }
    public bool IsAccessible { get; private set; }
    public ObservableCollection<FolderTreeItemViewModel> Children { get; } = [];
    public Task LoadingTask { get; private set; } = Task.CompletedTask;
    public bool IsExpanded
    {
        get => _expanded;
        set { if (SetProperty(ref _expanded, value) && value) LoadingTask = LoadAsync(); }
    }
    public bool IsSelected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value) && value && IsAccessible) _select(FullPath); }
    }
    private async Task LoadAsync()
    {
        if (_loaded || !IsAccessible) return;
        _loaded = true;
        try
        {
            var entries = await _catalog.GetChildrenAsync(FullPath, _token);
            if (_token.IsCancellationRequested) return;
            Children.Clear();
            foreach (var entry in entries) Children.Add(new FolderTreeItemViewModel(entry, _catalog, _select, _token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Folder browsing failed: {0}", ex);
            Details = ex.Message;
            _loaded = false;
            Children.Clear();
            OnPropertyChanged(nameof(Details));
        }
    }
}
