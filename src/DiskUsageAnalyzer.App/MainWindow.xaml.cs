using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.App.Theming;
using DiskUsageAnalyzer.App.ViewModels;
using DiskUsageAnalyzer.Infrastructure.Export;
using DiskUsageAnalyzer.Infrastructure.Scanning;
using DiskUsageAnalyzer.Infrastructure.Watching;
using DiskUsageAnalyzer.Infrastructure.Caching;

namespace DiskUsageAnalyzer.App;

public partial class MainWindow : Window
{
    private const int WmSettingChange = 0x001A;
    private readonly ThemeManager _themeManager;
    private HwndSource? _windowSource;
    private TreeViewItem? _revealedFolder;
    private double _treeVerticalOffset, _treeHorizontalOffset;
    public static readonly RoutedUICommand OpenResultCommand = new("Open in Explorer", nameof(OpenResultCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CopyResultPathCommand = new("Copy path", nameof(CopyResultPathCommand), typeof(MainWindow));
    public static readonly RoutedUICommand DeleteResultCommand = new("Delete", nameof(DeleteResultCommand), typeof(MainWindow));
    public static readonly RoutedUICommand RescanItemCommand = new("Rescan", nameof(RescanItemCommand), typeof(MainWindow));

    public MainWindow() : this(CreateViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _themeManager = ((App)System.Windows.Application.Current).ThemeManager;
        _themeManager.Apply(_themeManager.Preference, persist: false);
        UpdateThemeMenuChecks();
        SourceInitialized += WindowSourceInitialized;
        CommandBindings.Add(new CommandBinding(OpenResultCommand, ExecuteOpenResult, CanExecuteOpenResult));
        CommandBindings.Add(new CommandBinding(CopyResultPathCommand, ExecuteCopyResultPath, CanExecuteCopyResultPath));
        CommandBindings.Add(new CommandBinding(DeleteResultCommand, ExecuteDeleteResult, CanExecuteDeleteResult));
        CommandBindings.Add(new CommandBinding(RescanItemCommand, ExecuteRescanItem, CanExecuteRescanItem));

        var initialPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            viewModel.SelectedPath = initialPath;
        }

        DataContext = viewModel;
        viewModel.ResultViewChanging += SaveTreePosition;
        viewModel.ResultViewChanged += RestoreTreePosition;
        Loaded += async (_, _) =>
        {
            await viewModel.LoadRootsAsync();
            if (!string.IsNullOrWhiteSpace(viewModel.SelectedPath)) await viewModel.LoadSelectedCacheAsync();
        };
    }

    private void ThemeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string value }
            && Enum.TryParse<ThemePreference>(value, out var preference))
        {
            _themeManager.Apply(preference);
            UpdateThemeMenuChecks();
        }
    }

    private void UpdateThemeMenuChecks()
    {
        SystemThemeMenuItem.IsChecked = _themeManager.Preference == ThemePreference.System;
        LightThemeMenuItem.IsChecked = _themeManager.Preference == ThemePreference.Light;
        DarkThemeMenuItem.IsChecked = _themeManager.Preference == ThemePreference.Dark;
    }

    private void ExitMenuItemClick(object sender, RoutedEventArgs e) => Close();

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        _themeManager.ApplyTitleBar(this);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSettingChange && _themeManager.Preference == ThemePreference.System)
            Dispatcher.BeginInvoke(_themeManager.RefreshSystemTheme);
        return IntPtr.Zero;
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskUsageAnalyzer", "scan-cache.db");
        return new(
        new ScanSessionCoordinator(new FileSystemDiskScanner(), new CsvDiskUsageExporter(), new FileSystemChangeMonitor(),
            cache: new SqliteScanCache(cachePath), journal: new WindowsUsnJournal()),
        new FolderPicker(), new CsvExportPicker(), new ElevatedRelauncher(), new FileActions(), new UserInteraction(),
        new UiDispatcher(), new FolderCatalog());
    }

    private void FolderSelected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem item)
            item.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (!item.IsSelected) return;
                _revealedFolder = item;
                item.BringIntoView(new Rect(0, 0, Math.Min(item.ActualWidth, 180), 38));
            });
    }

    private void FolderTreeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_revealedFolder is { IsSelected: true } item)
            item.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => item.BringIntoView(new Rect(0, 0, Math.Min(item.ActualWidth, 180), 38)));
    }

    private void ResultSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainWindowViewModel { LargestFiles: false } viewModel
            && e.NewValue is DiskItemViewModel item && item.Item.ItemType != Core.Models.DiskItemType.File)
            viewModel.SelectedPath = item.FullPath;
    }

    private void SaveTreePosition(bool largestFiles)
    {
        if (!largestFiles) return;
        _treeVerticalOffset = FindScrollViewer(ResultTree)?.VerticalOffset ?? 0;
        _treeHorizontalOffset = ResultScroller.HorizontalOffset;
    }

    private void RestoreTreePosition(bool largestFiles)
    {
        if (largestFiles) return;
        // Wait for WPF to realize the restored hierarchy before applying its saved offsets.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (DataContext is not MainWindowViewModel { LargestFiles: false }) return;
            ResultTree.UpdateLayout();
            FindScrollViewer(ResultTree)?.ScrollToVerticalOffset(_treeVerticalOffset);
            ResultScroller.ScrollToHorizontalOffset(_treeHorizontalOffset);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void ResultColumnDividerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid header } splitter) return;

        var columnToLeft = Grid.GetColumn(splitter) - 1;
        if (columnToLeft < 0 || columnToLeft >= header.ColumnDefinitions.Count) return;

        var realizedRows = FindVisualDescendants<Grid>(ResultTree)
            .Where(grid => Equals(grid.Tag, "ResultRow") && columnToLeft < grid.ColumnDefinitions.Count)
            .ToArray();

        header.ColumnDefinitions[columnToLeft].Width = GridLength.Auto;
        foreach (var row in realizedRows)
            row.ColumnDefinitions[columnToLeft].Width = GridLength.Auto;

        header.UpdateLayout();
        var contentWidth = Math.Ceiling(header.ColumnDefinitions[columnToLeft].ActualWidth);

        foreach (var row in realizedRows)
            row.ColumnDefinitions[columnToLeft].Width = new GridLength(0);
        header.ColumnDefinitions[columnToLeft].Width = new GridLength(contentWidth);
        e.Handled = true;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ResultViewChanging -= SaveTreePosition;
            viewModel.ResultViewChanged -= RestoreTreePosition;
        }
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }

    private void CanExecuteOpenResult(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = DataContext is MainWindowViewModel viewModel
            && viewModel.OpenFolderCommand.CanExecute(e.Parameter);
    }

    private void ExecuteOpenResult(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.OpenFolderCommand.CanExecute(e.Parameter))
        {
            viewModel.OpenFolderCommand.Execute(e.Parameter);
        }
    }

    private void CanExecuteCopyResultPath(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = DataContext is MainWindowViewModel viewModel
            && viewModel.CopyPathCommand.CanExecute(e.Parameter);
    }

    private void ExecuteCopyResultPath(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.CopyPathCommand.CanExecute(e.Parameter))
        {
            viewModel.CopyPathCommand.Execute(e.Parameter);
        }
    }

    private void CanExecuteDeleteResult(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = DataContext is MainWindowViewModel viewModel
            && viewModel.DeleteItemCommand.CanExecute(e.Parameter);
    }

    private void ExecuteDeleteResult(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.DeleteItemCommand.CanExecute(e.Parameter))
        {
            viewModel.DeleteItemCommand.Execute(e.Parameter);
        }
    }

    private void CanExecuteRescanItem(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = DataContext is MainWindowViewModel viewModel
            && viewModel.RescanItemCommand.CanExecute(e.Parameter);
    }

    private void ExecuteRescanItem(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.RescanItemCommand.CanExecute(e.Parameter))
            viewModel.RescanItemCommand.Execute(e.Parameter);
    }
}
