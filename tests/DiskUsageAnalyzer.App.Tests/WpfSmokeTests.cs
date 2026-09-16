using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskUsageAnalyzer.App.ViewModels;
using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.App.Theming;
using DiskUsageAnalyzer.Infrastructure.Scanning;
using DiskUsageAnalyzer.Infrastructure.Export;
using DiskUsageAnalyzer.Infrastructure.Watching;
using DiskUsageAnalyzer.Core.Caching;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Tests;
using Moq;

namespace DiskUsageAnalyzer.App.Tests;

public sealed class WpfSmokeFactAttribute : FactAttribute
{
    public WpfSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DUA_UI_SMOKE") != "1")
            Skip = "Set DUA_UI_SMOKE=1 to run the WPF rendering and real-filesystem workflow checks.";
    }
}

public sealed class WpfSmokeTests
{
    [WpfSmokeFact]
    public Task ScanFilterWatchAndRender()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                App? application = null;
                MainWindow? window = null;
                try
                {
                    using var temp = TemporaryDirectory.Create();
                    using var export = TemporaryDirectory.Create();
                    var exportPath = Path.Combine(export.Path, "scan.csv");
                    await File.WriteAllBytesAsync(Path.Combine(temp.Path, "first.bin"), new byte[1024]);
                    Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
                    await File.WriteAllBytesAsync(Path.Combine(temp.Path, "nested", "second.txt"), new byte[2048]);
                    application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.InitializeComponent();
                    var picker = new Mock<ICsvExportPicker>();
                    picker.Setup(p => p.PickOutputPath(It.IsAny<string>())).Returns(exportPath);
                    var cache = new Mock<IScanCache>();
                    cache.Setup(c => c.ReplaceAsync(It.IsAny<DiskItem>(), It.IsAny<ScanOptions>(),
                            It.IsAny<VolumeIdentity?>(), It.IsAny<JournalCheckpoint?>(), It.IsAny<DateTimeOffset>(),
                            It.IsAny<IProgress<ScanProgress>?>(), It.IsAny<CancellationToken>()))
                        .Returns(Task.CompletedTask);
                    cache.Setup(c => c.FindLatestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync((CachedScanMetadata?)null);
                    var vm = new MainWindowViewModel(new ScanSessionCoordinator(new FileSystemDiskScanner(),
                            new CsvDiskUsageExporter(), new FileSystemChangeMonitor(), cache: cache.Object),
                        new FolderPicker(), picker.Object, new ElevatedRelauncher(), new FileActions(), new UserInteraction(), new UiDispatcher(), new FolderCatalog());
                    window = new MainWindow(vm) { ShowInTaskbar = false, Width = 1240, Height = 760 };
                    window.Show();
                    vm.SelectedPath = temp.Path;
                    vm.AutoRefreshChanges = true;
                    await vm.ScanCommand.ExecuteAsync();
                    Assert.Equal(3072, vm.BytesScanned);
                    Assert.False(vm.DeletionEnabled);
                    var canceled = vm.ScanCommand.ExecuteAsync();
                    vm.CancelCommand.Execute(null);
                    await canceled;
                    Assert.Equal("Canceled", vm.Status);
                    Assert.Equal(3072, vm.BytesScanned);
                    await vm.ScanCommand.ExecuteAsync();
                    vm.ExtensionFilter = "txt";
                    await vm.FilterTask;
                    Assert.Single(vm.Results[0].Children);
                    vm.ExtensionFilter = null;
                    await vm.FilterTask;
                    await File.WriteAllBytesAsync(Path.Combine(temp.Path, "third.bin"), new byte[100]);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (vm.BytesScanned != 3172) await Task.Delay(100, timeout.Token);
                    Assert.Equal(3, vm.FilesScanned);
                    await vm.ExportCommand.ExecuteAsync();
                    Assert.Contains("third.bin", await File.ReadAllTextAsync(exportPath));
                    var artifacts = Environment.GetEnvironmentVariable("DUA_ARTIFACTS")
                        ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "ui");
                    Directory.CreateDirectory(artifacts);
                    vm.SelectedPath = Path.Combine(temp.Path, "nested");
                    vm.LargestFiles = true;
                    await vm.FilterTask;
                    await vm.RevealFolderTask;
                    Assert.Equal("second.txt", Assert.Single(vm.Results).Name);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var folderTree = (System.Windows.Controls.TreeView)window.FindName("FolderTree");
                    var selectedFolder = Descendants(folderTree).OfType<System.Windows.Controls.TreeViewItem>().Single(item => item.IsSelected);
                    Assert.Equal(vm.SelectedPath, ((FolderTreeItemViewModel)selectedFolder.DataContext).FullPath);
                    var position = selectedFolder.TransformToAncestor(folderTree).Transform(new Point());
                    Assert.InRange(position.Y, 0, folderTree.ActualHeight - 1);
                    Render(window, Path.Combine(artifacts, "desktop.png"));
                    window.Width = 800;
                    window.Height = 600;
                    window.UpdateLayout();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    position = selectedFolder.TransformToAncestor(folderTree).Transform(new Point());
                    Assert.InRange(position.Y, 0, folderTree.ActualHeight - 1);
                    Render(window, Path.Combine(artifacts, "narrow.png"));
                    var darkThemeMenuItem = (System.Windows.Controls.MenuItem)window.FindName("DarkThemeMenuItem");
                    darkThemeMenuItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(ThemePreference.Dark, application.ThemeManager.Preference);
                    Assert.True(application.ThemeManager.IsDarkTheme);
                    Assert.Equal(Color.FromRgb(0x17, 0x1A, 0x1F),
                        ((SolidColorBrush)application.Resources["WindowBackgroundBrush"]).Color);
                    Assert.True(darkThemeMenuItem.IsChecked);
                    Render(window, Path.Combine(artifacts, "dark.png"));
                    var darkResultTree = (System.Windows.Controls.TreeView)window.FindName("ResultTree");
                    var darkResultItem = Assert.IsType<System.Windows.Controls.TreeViewItem>(
                        darkResultTree.ItemContainerGenerator.ContainerFromIndex(0));
                    var contextMenu = Assert.IsType<System.Windows.Controls.ContextMenu>(darkResultItem.ContextMenu);
                    contextMenu.PlacementTarget = darkResultItem;
                    contextMenu.IsOpen = true;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var menuItems = contextMenu.Items.OfType<System.Windows.Controls.MenuItem>().ToArray();
                    Assert.Equal(4, menuItems.Length);
                    Assert.All(menuItems, item => Assert.IsType<System.Windows.Controls.TextBlock>(item.Icon));
                    Render(contextMenu, Path.Combine(artifacts, "dark-context-menu.png"));
                    contextMenu.IsOpen = false;
                    var cacheLoad = new TaskCompletionSource<CachedScan?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    cache.Setup(c => c.LoadLatestAsync(vm.SelectedPath!, It.IsAny<IProgress<ScanProgress>?>(),
                            It.IsAny<CancellationToken>())).Returns(cacheLoad.Task);
                    var cacheLoading = vm.LoadCacheCommand.ExecuteAsync();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var cacheOverlay = (System.Windows.Controls.Border)window.FindName("CacheLoadingOverlay");
                    Assert.Equal(Visibility.Visible, cacheOverlay.Visibility);
                    Render(window, Path.Combine(artifacts, "dark-cache-loading.png"));
                    cacheLoad.SetResult(null);
                    await cacheLoading;
                    Assert.Equal(Visibility.Collapsed, cacheOverlay.Visibility);
                    var systemThemeMenuItem = (System.Windows.Controls.MenuItem)window.FindName("SystemThemeMenuItem");
                    systemThemeMenuItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    vm.AutoRefreshChanges = false;
                    for (var index = 0; index < 80; index++)
                        await File.WriteAllBytesAsync(Path.Combine(temp.Path, $"extra-{index:D2}.bin"), new byte[10]);
                    vm.LargestFiles = false;
                    await vm.FilterTask;
                    vm.SelectedPath = temp.Path;
                    await vm.ScanCommand.ExecuteAsync();
                    var nestedRow = vm.Results[0].Children.Single(item => item.Name == "nested");
                    nestedRow.IsExpanded = true;
                    nestedRow.IsSelected = true;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var resultTree = (System.Windows.Controls.TreeView)window.FindName("ResultTree");
                    var resultScroll = Descendants(resultTree).OfType<System.Windows.Controls.ScrollViewer>().First();
                    var horizontalScroll = (System.Windows.Controls.ScrollViewer)window.FindName("ResultScroller");
                    resultScroll.ScrollToVerticalOffset(35);
                    horizontalScroll.ScrollToHorizontalOffset(100);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var verticalOffset = resultScroll.VerticalOffset;
                    var horizontalOffset = horizontalScroll.HorizontalOffset;
                    Assert.True(verticalOffset > 0);
                    vm.LargestFiles = true;
                    await vm.FilterTask;
                    vm.LargestFiles = false;
                    await vm.FilterTask;
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.InRange(resultScroll.VerticalOffset, verticalOffset - 0.5, verticalOffset + 0.5);
                    Assert.InRange(horizontalScroll.HorizontalOffset, horizontalOffset - 0.5, horizontalOffset + 0.5);
                    nestedRow = vm.Results[0].Children.Single(item => item.Name == "nested");
                    Assert.True(nestedRow.IsExpanded);
                    Assert.True(nestedRow.IsSelected);
                    window.Close();
                    window = null;
                    completion.SetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { window?.Close(); application?.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Render(Window window, string path)
        => Render((FrameworkElement)window, path);

    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Contains(pixels, value => value != 0);
        Assert.True(pixels.Where((_, index) => index % 4 == 0).Distinct().Count() > 10);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
