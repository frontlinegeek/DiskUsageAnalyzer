using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Input;
using DiskUsageAnalyzer.App.Commands;
using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.Core.Formatting;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;

namespace DiskUsageAnalyzer.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ScanSessionCoordinator _session;
    private readonly IFolderPicker _folderPicker;
    private readonly ICsvExportPicker _exportPicker;
    private readonly IElevatedRelauncher _elevation;
    private readonly IFileActions _actions;
    private readonly IUserInteraction _interaction;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFolderCatalog _folders;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _filterCancellation;
    private string? _selectedPath, _currentPath, _searchText, _extensionFilter;
    private string _minimumSizeText = "", _status = "Ready", _operation = "";
    private long _filesScanned, _foldersScanned, _bytesScanned, _errorsEncountered, _operationId;
    private bool _watch, _deletionEnabled, _disposed, _refreshFailed, _acceptProgress;
    private string? _monitoringError;
    private bool _largestFiles, _smallestFirst;
    private long _revealVersion;
    private bool _resultsAreLargestFiles;
    private HashSet<string>? _treeExpandedPaths;
    private string? _treeSelectedPath;

    public event Action<bool>? ResultViewChanging;
    public event Action<bool>? ResultViewChanged;

    public MainWindowViewModel(ScanSessionCoordinator session, IFolderPicker folderPicker, ICsvExportPicker exportPicker,
        IElevatedRelauncher elevation, IFileActions actions, IUserInteraction interaction, IUiDispatcher dispatcher,
        IFolderCatalog folders, TimeProvider? time = null)
    {
        _session = session; _folderPicker = folderPicker; _exportPicker = exportPicker; _elevation = elevation;
        _actions = actions; _interaction = interaction; _dispatcher = dispatcher; _folders = folders;
        _time = time ?? TimeProvider.System;
        BrowseCommand = new RelayCommand(_ => Safe(() =>
        {
            var path = _folderPicker.PickFolder(SelectedPath);
            if (!string.IsNullOrWhiteSpace(path)) SelectScanRoot(path);
        }), _ => !IsBusy);
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPath), Failure);
        LoadCacheCommand = new AsyncRelayCommand(_ => LoadCacheAsync(false), _ => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPath), Failure);
        IncrementalRefreshCommand = new AsyncRelayCommand(_ => IncrementalRefreshAsync(), _ => !IsBusy && _session.CacheMetadata is not null, Failure);
        DeleteCacheCommand = new AsyncRelayCommand(_ => DeleteCacheAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPath), Failure);
        CancelCommand = new RelayCommand(_ => _operationCancellation?.Cancel(), _ => IsBusy);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => !IsBusy && _session.Snapshot is not null, Failure);
        CopyPathCommand = new RelayCommand(p => Safe(() => _actions.CopyPath(((DiskItemViewModel)p!).FullPath)), p => p is DiskItemViewModel);
        OpenFolderCommand = new RelayCommand(p => Safe(() =>
        {
            var item = (DiskItemViewModel)p!;
            _actions.OpenFolder(item.Item.ItemType == DiskItemType.File ? Path.GetDirectoryName(item.FullPath)! : item.FullPath);
        }), p => p is DiskItemViewModel);
        DeleteItemCommand = new AsyncRelayCommand(p => DeleteAsync((DiskItemViewModel)p!), p => CanDelete(p as DiskItemViewModel), Failure);
        RescanItemCommand = new AsyncRelayCommand(RescanAsync, CanRescan, Failure);
        ElevateCommand = new RelayCommand(_ => Safe(() =>
        {
            if (_elevation.Relaunch(SelectedPath) is not null) _interaction.Shutdown();
        }), _ => !IsBusy && CanElevate);
        RefreshFoldersCommand = new AsyncRelayCommand(_ => LoadRootsAsync(), _ => !IsBusy, Failure);
        _session.ChangesPending += OnChangesPending;
        _session.MonitoringFailed += OnMonitoringFailed;
    }

    public ObservableCollection<DiskItemViewModel> Results { get; } = [];
    public ObservableCollection<FolderTreeItemViewModel> FolderRoots { get; } = [];
    public ICommand BrowseCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand LoadCacheCommand { get; }
    public AsyncRelayCommand IncrementalRefreshCommand { get; }
    public AsyncRelayCommand DeleteCacheCommand { get; }
    public ICommand CancelCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public AsyncRelayCommand DeleteItemCommand { get; }
    public AsyncRelayCommand RescanItemCommand { get; }
    public ICommand ElevateCommand { get; }
    public AsyncRelayCommand RefreshFoldersCommand { get; }
    public Task FilterTask { get; private set; } = Task.CompletedTask;
    public bool IsAdministrator { get; } = GetAdministrator();
    public bool CanElevate => !IsAdministrator;
    public string AccessLevelText => IsAdministrator ? "Administrator access" : "Standard access";
    public string? SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (!SetProperty(ref _selectedPath, value)) return;
            RaiseCommands();
            if (LargestFiles) { ScheduleFilter(false); RevealFolderTask = RevealFolderAsync(); }
        }
    }
    public bool LargestFiles
    {
        get => _largestFiles;
        set
        {
            if (!SetProperty(ref _largestFiles, value)) return;
            OnPropertyChanged(nameof(PercentageHeading));
            ScheduleFilter(false);
            if (value) RevealFolderTask = RevealFolderAsync();
            else _revealVersion++;
        }
    }
    public bool SmallestFirst
    {
        get => _smallestFirst;
        set { if (SetProperty(ref _smallestFirst, value)) ScheduleFilter(false); }
    }
    public string PercentageHeading => LargestFiles ? "Selected folder %" : "Parent %";
    public Task RevealFolderTask { get; private set; } = Task.CompletedTask;
    public string? CurrentPath { get => _currentPath; private set => SetProperty(ref _currentPath, value); }
    public string? SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ScheduleFilter(); } }
    public string? ExtensionFilter { get => _extensionFilter; set { if (SetProperty(ref _extensionFilter, value)) ScheduleFilter(); } }
    public string MinimumSizeText { get => _minimumSizeText; set { if (SetProperty(ref _minimumSizeText, value)) ScheduleFilter(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public long FilesScanned => _filesScanned;
    public long FoldersScanned => _foldersScanned;
    public long BytesScanned => _bytesScanned;
    public long ErrorsEncountered => _errorsEncountered;
    public string FilesScannedText => _filesScanned.ToString("N0");
    public string FoldersScannedText => _foldersScanned.ToString("N0");
    public string BytesScannedText => SizeFormatter.Format(_bytesScanned);
    public string ErrorsEncounteredText => _errorsEncountered.ToString("N0");
    public bool IsBusy => _operation.Length != 0;
    public bool IsScanning => _operation == "Scanning";
    public bool IsLoadingCache => _operation == "Loading cache";
    public bool IsDeleting => _operation == "Deleting";
    public bool IsCachedData => _session.IsDisplayingCachedData;
    public string CacheStatus => _session.CacheMetadata is { } cache
        ? $"Cached results · updated {cache.CompletedAt.ToLocalTime():g}" : "";
    public bool DeletionEnabled
    {
        get => _deletionEnabled;
        set { if (SetProperty(ref _deletionEnabled, value)) RaiseCommands(); }
    }
    public bool AutoRefreshChanges
    {
        get => _watch;
        set
        {
            if (!SetProperty(ref _watch, value)) return;
            _session.SetWatching(value);
        }
    }

    public async Task LoadRootsAsync()
    {
        try
        {
            var entries = await _folders.GetRootsAsync(_lifetime.Token);
            if (_disposed) return;
            FolderRoots.Clear();
            foreach (var entry in entries)
                FolderRoots.Add(new FolderTreeItemViewModel(entry, _folders, SelectScanRoot, _lifetime.Token));
            if (LargestFiles) await (RevealFolderTask = RevealFolderAsync());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure(ex); }
    }

    private async Task ScanAsync()
    {
        var path = SelectedPath!;
        _refreshFailed = false;
        _monitoringError = null;
        await RunOperation("Scanning", async token =>
        {
            var operationId = _operationId;
            _acceptProgress = true;
            var elapsed = Stopwatch.StartNew();
            UpdateProgress(new ScanProgress());
            var progress = new Progress<ScanProgress>(update =>
            {
                if (!_disposed && _acceptProgress && IsScanning && operationId == _operationId) UpdateProgress(update);
            });
            await _session.ScanAsync(new ScanOptions { RootPath = path, IncludeHiddenItems = true, CalculateAllocatedSize = true }, progress, token);
            _acceptProgress = false;
            await PublishAsync();
            NotifyCacheState();
            Status = Completion($"Complete in {elapsed.Elapsed:g}");
        });
    }

    public Task LoadSelectedCacheAsync() => LoadCacheAsync(true);

    private async Task LoadCacheAsync(bool automatic)
    {
        if (string.IsNullOrWhiteSpace(SelectedPath) || IsBusy) return;
        var previousStatus = Status;
        var requestedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SelectedPath));
        await RunOperation("Loading cache", async token =>
        {
            var operationId = _operationId;
            _acceptProgress = true;
            UpdateProgress(new ScanProgress { CurrentPath = requestedPath });
            var progress = new Progress<ScanProgress>(update =>
            {
                if (!_disposed && _acceptProgress && IsLoadingCache && operationId == _operationId)
                    UpdateProgress(update);
            });
            var loaded = await _session.LoadCachedAsync(requestedPath, progress, token);
            _acceptProgress = false;
            if (!loaded)
            {
                Status = automatic ? previousStatus : "No cached scan is available for this location";
                return;
            }
            if (!string.Equals(requestedPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(SelectedPath!)), StringComparison.OrdinalIgnoreCase)) return;
            await PublishAsync();
            NotifyCacheState();
            Status = CacheStatus;
        });
    }

    private void SelectScanRoot(string path)
    {
        SelectedPath = path;
        _dispatcher.Post(() => { if (!_disposed && !IsBusy) _ = LoadCacheAsync(true); });
    }

    private async Task IncrementalRefreshAsync()
    {
        await RunOperation("Refreshing cached scan", async token =>
        {
            var operationId = _operationId;
            _acceptProgress = true;
            var progress = new Progress<ScanProgress>(update =>
            {
                if (!_disposed && _acceptProgress && operationId == _operationId) UpdateProgress(update);
            });
            var result = await _session.RefreshCachedAsync(progress, token);
            _acceptProgress = false;
            await PublishAsync();
            NotifyCacheState();
            Status = result.Kind == Core.Caching.CacheRefreshKind.FullScan
                ? $"Full scan complete ({result.Detail})" : $"Incremental refresh complete · {result.Detail}";
        });
    }

    private async Task DeleteCacheAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;
        await RunOperation("Deleting cache", async token =>
        {
            await _session.DeleteCacheAsync(SelectedPath, token);
            NotifyCacheState();
            Status = "Cached scan deleted";
        });
    }

    private void NotifyCacheState()
    {
        OnPropertyChanged(nameof(IsCachedData));
        OnPropertyChanged(nameof(CacheStatus));
        RaiseCommands();
    }

    private async Task RevealFolderAsync()
    {
        var version = ++_revealVersion;
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedPath)) return;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SelectedPath));
            bool Contains(FolderTreeItemViewModel item) => !string.IsNullOrEmpty(item.FullPath)
                && (string.Equals(Path.TrimEndingDirectorySeparator(item.FullPath), path, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(item.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var node = FolderRoots.FirstOrDefault(Contains);
            while (node is not null && !_disposed && version == _revealVersion)
            {
                node.IsExpanded = true;
                await node.LoadingTask;
                if (_disposed || version != _revealVersion) return;
                if (string.Equals(Path.TrimEndingDirectorySeparator(node.FullPath), path, StringComparison.OrdinalIgnoreCase))
                {
                    node.IsSelected = true;
                    return;
                }
                node = node.Children.FirstOrDefault(Contains);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure(ex); }
    }

    private async Task ExportAsync()
    {
        var root = _session.Snapshot!.Rows[_session.Snapshot.RootPath];
        var name = string.Join("_", root.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var path = _exportPicker.PickOutputPath(name + "-disk-usage.csv");
        if (string.IsNullOrWhiteSpace(path)) return;
        await RunOperation("Exporting", async token =>
        {
            await _session.ExportAsync(path, token);
            Status = $"Exported {path}";
        });
    }

    private bool CanDelete(DiskItemViewModel? item) => !_disposed && DeletionEnabled && !IsBusy
        && item is { IsReparsePoint: false } && item.Item.ItemType != DiskItemType.Drive
        && _session.Snapshot is { } snapshot && snapshot.Rows.ContainsKey(item.FullPath)
        && !string.Equals(item.FullPath, snapshot.RootPath, StringComparison.OrdinalIgnoreCase);

    private async Task DeleteAsync(DiskItemViewModel item)
    {
        if (!CanDelete(item) || !_interaction.ConfirmDeletion(item.FullPath) || !CanDelete(item)) return;
        await RunOperation("Deleting", async token =>
        {
            await _session.DeleteAsync(item.FullPath, _actions, token);
            await PublishAsync();
            Status = Completion($"Deleted {item.FullPath}");
        });
    }

    private bool CanRescan(object? parameter) => !_disposed && !IsBusy && parameter switch
    {
        DiskItemViewModel item => item.Item.ItemType != DiskItemType.File && !item.IsReparsePoint
            && _session.Snapshot?.Rows.ContainsKey(item.FullPath) == true,
        FolderTreeItemViewModel folder => folder.IsAccessible && !string.IsNullOrWhiteSpace(folder.FullPath),
        _ => false
    };

    private async Task RescanAsync(object? parameter)
    {
        if (!CanRescan(parameter)) return;
        if (parameter is FolderTreeItemViewModel folder)
        {
            SelectedPath = folder.FullPath;
            await ScanAsync();
            return;
        }

        var item = (DiskItemViewModel)parameter!;
        await RunOperation("Rescanning", async token =>
        {
            var operationId = _operationId;
            _acceptProgress = true;
            var elapsed = Stopwatch.StartNew();
            var progress = new Progress<ScanProgress>(update =>
            {
                if (!_disposed && _acceptProgress && operationId == _operationId) UpdateProgress(update);
            });
            await _session.RescanAsync(item.FullPath, progress, token);
            _acceptProgress = false;
            await PublishAsync();
            NotifyCacheState();
            Status = Completion($"Rescanned {item.FullPath} in {elapsed.Elapsed:g}");
        });
    }

    private async Task RunOperation(string operation, Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        _operation = operation;
        _operationId++;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        BusyChanged();
        Status = operation;
        try { await action(_operationCancellation.Token); }
        catch (OperationCanceledException)
        {
            _acceptProgress = false;
            _refreshFailed = true;
            await PublishAsync();
            if (!_disposed) Status = "Canceled";
        }
        catch (Exception ex)
        {
            _acceptProgress = false;
            _refreshFailed = true;
            await PublishAsync();
            Failure(ex);
        }
        finally
        {
            _operation = "";
            _acceptProgress = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            BusyChanged();
            if (!_disposed && !_refreshFailed && _session.HasPending) _dispatcher.Post(() => _ = RefreshAsync());
        }
    }

    private void OnChangesPending() => _dispatcher.Post(() =>
    {
        if (_disposed) return;
        if (!IsBusy) { _refreshFailed = false; _ = RefreshAsync(); }
    });

    private void OnMonitoringFailed(string message) => _dispatcher.Post(() =>
    {
        if (_disposed) return;
        _monitoringError = message;
        Status = $"Live monitoring unavailable: {message}";
    });

    private async Task RefreshAsync()
    {
        if (_disposed || IsBusy || !AutoRefreshChanges || !_session.HasPending) return;
        await RunOperation("Refreshing", async token =>
        {
            await _session.RefreshAsync(token);
            await PublishAsync();
            Status = Completion("Auto refresh complete");
        });
    }

    private string Completion(string status) => status
        + (_errorsEncountered > 0 ? " (incomplete: scan errors)" : "")
        + (_monitoringError is not null ? " (live monitoring unavailable)" : "");

    private async Task PublishAsync()
    {
        if (_disposed || _session.Snapshot is not { } snapshot) return;
        var root = snapshot.Rows[snapshot.RootPath];
        UpdateProgress(new ScanProgress
        {
            CurrentPath = root.FullPath,
            FilesScanned = root.FileCount,
            FoldersScanned = root.FolderCount,
            BytesScanned = root.SizeBytes,
            ErrorsEncountered = root.ErrorCount
        });
        ScheduleFilter(false);
        await FilterTask;
    }

    private void UpdateProgress(ScanProgress progress)
    {
        CurrentPath = progress.CurrentPath;
        _filesScanned = progress.FilesScanned; _foldersScanned = progress.FoldersScanned;
        _bytesScanned = progress.BytesScanned; _errorsEncountered = progress.ErrorsEncountered;
        foreach (var name in new[] { nameof(FilesScanned), nameof(FoldersScanned), nameof(BytesScanned), nameof(ErrorsEncountered),
            nameof(FilesScannedText), nameof(FoldersScannedText), nameof(BytesScannedText), nameof(ErrorsEncounteredText) }) OnPropertyChanged(name);
    }

    private void ScheduleFilter(bool debounce = true)
    {
        _filterCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _filterCancellation = cancellation;
        FilterTask = FilterAsync(cancellation, debounce);
    }

    private async Task FilterAsync(CancellationTokenSource cancellation, bool debounce)
    {
        try
        {
            if (debounce) await Task.Delay(TimeSpan.FromMilliseconds(250), _time, cancellation.Token);
            var snapshot = _session.Snapshot;
            if (snapshot is null) return;
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? selected = null;
            var pending = new Stack<DiskItemViewModel>(Results);
            while (pending.TryPop(out var item))
            {
                if (item.IsExpanded) expanded.Add(item.FullPath);
                if (item.IsSelected) selected = item.FullPath;
                foreach (var child in item.LoadedChildren) pending.Push(child);
            }
            if (!_resultsAreLargestFiles && Results.Count > 0)
            {
                _treeExpandedPaths = expanded;
                _treeSelectedPath = selected;
            }
            var search = SearchText;
            var extension = ExtensionFilter;
            var minimum = long.TryParse(MinimumSizeText, out var size) ? Math.Max(0, size) : 0;
            var largestFiles = LargestFiles;
            var folderPath = SelectedPath;
            var smallestFirst = SmallestFirst;
            var (visible, files) = await Task.Run(() =>
            {
                var matches = snapshot.Filter(search, extension, minimum, cancellation.Token);
                return (matches, largestFiles ? snapshot.FilesUnder(folderPath, matches, smallestFirst, cancellation.Token) : []);
            }, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(snapshot, _session.Snapshot)) return;
            var viewChanged = _resultsAreLargestFiles != largestFiles;
            if (viewChanged) ResultViewChanging?.Invoke(largestFiles);
            Results.Clear();
            var root = snapshot.Rows[snapshot.RootPath];
            if (largestFiles)
            {
                var folderSize = folderPath is not null && snapshot.Rows.TryGetValue(Path.TrimEndingDirectorySeparator(folderPath), out var folder)
                    ? folder.SizeBytes : 0;
                foreach (var file in files) Results.Add(new DiskItemViewModel(file, folderSize, snapshot, visible, selectedPath: selected));
            }
            else Results.Add(new DiskItemViewModel(root, root.SizeBytes, snapshot, visible, _treeExpandedPaths, _treeSelectedPath));
            _resultsAreLargestFiles = largestFiles;
            if (viewChanged) ResultViewChanged?.Invoke(largestFiles);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failure(ex); }
        finally { if (ReferenceEquals(_filterCancellation, cancellation)) _filterCancellation = null; cancellation.Dispose(); }
    }

    private void BusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsLoadingCache)); OnPropertyChanged(nameof(IsDeleting));
        RaiseCommands();
    }
    private void RaiseCommands()
    {
        foreach (var command in new[] { BrowseCommand, ScanCommand, LoadCacheCommand, IncrementalRefreshCommand, DeleteCacheCommand,
                     CancelCommand, ExportCommand, DeleteItemCommand, RescanItemCommand, ElevateCommand, RefreshFoldersCommand })
        {
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
            if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged();
        }
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { Failure(ex); } }
    private void Failure(Exception ex)
    {
        Trace.TraceError("Operation failed: {0}", ex);
        if (!_disposed) Status = ex is Win32Exception { NativeErrorCode: 1223 } ? "Elevation canceled" : ex.Message;
    }
    private static bool GetAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _session.ChangesPending -= OnChangesPending;
        _session.MonitoringFailed -= OnMonitoringFailed;
        _session.Dispose();
    }
}
