using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.App.ViewModels;
using DiskUsageAnalyzer.Infrastructure.Export;
using DiskUsageAnalyzer.Infrastructure.Scanning;
using DiskUsageAnalyzer.Infrastructure.Watching;

namespace DiskUsageAnalyzer.App;

public partial class MainWindow : Window
{
    private TreeViewItem? _revealedFolder;
    private double _treeVerticalOffset, _treeHorizontalOffset;
    public static readonly RoutedUICommand OpenResultCommand = new("Open in Explorer", nameof(OpenResultCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CopyResultPathCommand = new("Copy path", nameof(CopyResultPathCommand), typeof(MainWindow));
    public static readonly RoutedUICommand DeleteResultCommand = new("Delete", nameof(DeleteResultCommand), typeof(MainWindow));

    public MainWindow() : this(CreateViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(OpenResultCommand, ExecuteOpenResult, CanExecuteOpenResult));
        CommandBindings.Add(new CommandBinding(CopyResultPathCommand, ExecuteCopyResultPath, CanExecuteCopyResultPath));
        CommandBindings.Add(new CommandBinding(DeleteResultCommand, ExecuteDeleteResult, CanExecuteDeleteResult));

        var initialPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            viewModel.SelectedPath = initialPath;
        }

        DataContext = viewModel;
        viewModel.ResultViewChanging += SaveTreePosition;
        viewModel.ResultViewChanged += RestoreTreePosition;
        Loaded += async (_, _) => await viewModel.LoadRootsAsync();
    }

    private static MainWindowViewModel CreateViewModel() => new(
        new ScanSessionCoordinator(new FileSystemDiskScanner(), new CsvDiskUsageExporter(), new FileSystemChangeMonitor()),
        new FolderPicker(), new CsvExportPicker(), new ElevatedRelauncher(), new FileActions(), new UserInteraction(),
        new UiDispatcher(), new FolderCatalog());

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

    protected override void OnClosed(EventArgs e)
    {
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
}
