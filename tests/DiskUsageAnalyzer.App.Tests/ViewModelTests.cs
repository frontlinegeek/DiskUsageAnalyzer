using System.IO;
using DiskUsageAnalyzer.App.Commands;
using DiskUsageAnalyzer.App.Services;
using DiskUsageAnalyzer.App.ViewModels;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Tests;
using Moq;

namespace DiskUsageAnalyzer.App.Tests;

public sealed class ViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargestFiles_RestoresTreeStateAcrossRepeatedToggles(bool expanded)
    {
        var root = Fixture.Tree();
        var folder = new DiskItem { Name = "nested", FullPath = Path.Combine(root.FullPath, "nested"), ItemType = DiskItemType.Directory };
        folder.Children.Add(new DiskItem { Name = "child.bin", FullPath = Path.Combine(folder.FullPath, "child.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = 5 });
        root.Children.Add(folder);
        DiskTree.Recalculate(root);
        var scanner = new Mock<IDiskScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(root);
        using var vm = new MainWindowViewModel(new ScanSessionCoordinator(scanner.Object, Mock.Of<IDiskUsageExporter>(), Mock.Of<IFileSystemChangeMonitor>()),
            Mock.Of<IFolderPicker>(), Mock.Of<ICsvExportPicker>(), Mock.Of<IElevatedRelauncher>(), Mock.Of<IFileActions>(),
            Mock.Of<IUserInteraction>(), Mock.Of<IUiDispatcher>(), Mock.Of<IFolderCatalog>());
        vm.SelectedPath = root.FullPath;
        await vm.ScanCommand.ExecuteAsync();
        var treeFolder = vm.Results[0].Children.Single(item => item.FullPath == folder.FullPath);
        treeFolder.IsExpanded = expanded;
        treeFolder.IsSelected = true;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            vm.LargestFiles = true;
            await vm.FilterTask;
            vm.Results[0].IsSelected = true;
            vm.SmallestFirst = !vm.SmallestFirst;
            await vm.FilterTask;
            vm.SelectedPath = folder.FullPath;
            await vm.FilterTask;
            vm.LargestFiles = false;
            await vm.FilterTask;
            treeFolder = vm.Results[0].Children.Single(item => item.FullPath == folder.FullPath);
            Assert.True(vm.Results[0].IsExpanded);
            Assert.Equal(expanded, treeFolder.IsExpanded);
            Assert.True(treeFolder.IsSelected);
            Assert.False(vm.Results[0].Children.Single(item => item.Item.ItemType == DiskItemType.File).IsSelected);
        }
        vm.Results[0].IsExpanded = false;
        vm.LargestFiles = true;
        await vm.FilterTask;
        vm.LargestFiles = false;
        await vm.FilterTask;
        Assert.False(vm.Results[0].IsExpanded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargestFiles_CanToggleBeforeOrAfterScan(bool beforeScan)
    {
        using var fixture = new Fixture();
        fixture.ViewModel.LargestFiles = beforeScan;
        await fixture.ViewModel.ScanCommand.ExecuteAsync();
        fixture.ViewModel.LargestFiles = true;
        await fixture.ViewModel.FilterTask;
        Assert.Equal(DiskItemType.File, Assert.Single(fixture.ViewModel.Results).Item.ItemType);
        fixture.ViewModel.SelectedPath = Path.Combine(Fixture.Tree().FullPath, "unscanned");
        await fixture.ViewModel.FilterTask;
        Assert.Empty(fixture.ViewModel.Results);
        fixture.ViewModel.LargestFiles = false;
        await fixture.ViewModel.FilterTask;
        Assert.Equal(DiskItemType.Directory, Assert.Single(fixture.ViewModel.Results).Item.ItemType);
    }

    [Fact]
    public void LargestFiles_OnlyIncludesSubtreeAndHonorsOrderAndFilters()
    {
        var root = Fixture.Tree();
        var folder = new DiskItem { Name = "selected", FullPath = Path.Combine(root.FullPath, "selected"), ItemType = DiskItemType.Directory };
        var nested = new DiskItem { Name = "nested", FullPath = Path.Combine(folder.FullPath, "nested"), ItemType = DiskItemType.Directory };
        folder.Children.Add(new DiskItem { Name = "small.bin", FullPath = Path.Combine(folder.FullPath, "small.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = 1 });
        nested.Children.Add(new DiskItem { Name = "large.txt", FullPath = Path.Combine(nested.FullPath, "large.txt"), ItemType = DiskItemType.File, LogicalSizeBytes = 100 });
        folder.Children.Add(nested);
        root.Children.Add(folder);
        root.Children.Add(new DiskItem { Name = "selected-other", FullPath = Path.Combine(root.FullPath, "selected-other"), ItemType = DiskItemType.Directory });
        DiskTree.Recalculate(root);
        var snapshot = ResultSnapshot.Create(root);
        var visible = snapshot.Filter(null, null, 0, default);
        Assert.Equal(new[] { "large.txt", "small.bin" }, snapshot.FilesUnder(folder.FullPath, visible, false, default).Select(r => r.Name));
        Assert.Equal(new[] { "small.bin", "large.txt" }, snapshot.FilesUnder(folder.FullPath, visible, true, default).Select(r => r.Name));
        Assert.Equal("large.txt", Assert.Single(snapshot.FilesUnder(folder.FullPath, snapshot.Filter("large", "txt", 10, default), false, default)).Name);
        Assert.Empty(snapshot.FilesUnder(Path.Combine(root.FullPath, "selected-other"), visible, false, default));
    }

    [Fact]
    public async Task LargestFiles_ExpandsAncestorsAndSelectsScope()
    {
        var catalog = new Mock<IFolderCatalog>();
        catalog.Setup(c => c.GetRootsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new FolderEntry("drive", @"C:\", "")]);
        catalog.Setup(c => c.GetChildrenAsync(@"C:\", It.IsAny<CancellationToken>())).ReturnsAsync([new FolderEntry("parent", @"C:\parent", "")]);
        catalog.Setup(c => c.GetChildrenAsync(@"C:\parent", It.IsAny<CancellationToken>())).ReturnsAsync([new FolderEntry("target", @"C:\parent\target", "")]);
        catalog.Setup(c => c.GetChildrenAsync(@"C:\parent\target", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var vm = new MainWindowViewModel(new ScanSessionCoordinator(Mock.Of<IDiskScanner>(), Mock.Of<IDiskUsageExporter>(), Mock.Of<IFileSystemChangeMonitor>()),
            Mock.Of<IFolderPicker>(), Mock.Of<ICsvExportPicker>(), Mock.Of<IElevatedRelauncher>(), Mock.Of<IFileActions>(),
            Mock.Of<IUserInteraction>(), Mock.Of<IUiDispatcher>(), catalog.Object);
        vm.SelectedPath = @"C:\parent\target";
        vm.LargestFiles = true;
        await vm.LoadRootsAsync();
        await vm.RevealFolderTask;
        var drive = Assert.Single(vm.FolderRoots);
        var parent = Assert.Single(drive.Children);
        var target = Assert.Single(parent.Children);
        Assert.True(drive.IsExpanded);
        Assert.True(parent.IsExpanded);
        Assert.True(target.IsExpanded);
        Assert.True(target.IsSelected);
    }

    [Fact]
    public async Task AsyncCommand_PreventsReentryAndRestoresStateAfterFailure()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var command = new AsyncRelayCommand(async _ => { calls++; await completion.Task; });
        var first = command.ExecuteAsync();
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, calls);
        completion.SetException(new IOException("failure"));
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.True(command.CanExecute(null));
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public async Task Deletion_RequiresOptInAndConfirmation(bool enabled, bool confirmed, int calls)
    {
        using var fixture = new Fixture();
        fixture.Interaction.Setup(i => i.ConfirmDeletion(It.IsAny<string>())).Returns(confirmed);
        fixture.Actions.Setup(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), DiskItemType.File, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await fixture.ViewModel.ScanCommand.ExecuteAsync();
        var child = fixture.ViewModel.Results[0].Children[0];
        Assert.False(fixture.ViewModel.DeletionEnabled);
        fixture.ViewModel.DeletionEnabled = enabled;
        await fixture.ViewModel.DeleteItemCommand.ExecuteAsync(child);
        fixture.Actions.Verify(a => a.DeleteAsync(It.IsAny<string>(), child.FullPath, DiskItemType.File, It.IsAny<CancellationToken>()), Times.Exactly(calls));
        if (!enabled) fixture.Interaction.Verify(i => i.ConfirmDeletion(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExportFailure_IsVisibleAndDoesNotLeaveBusyState()
    {
        using var fixture = new Fixture();
        fixture.ExportPicker.Setup(p => p.PickOutputPath(It.IsAny<string>())).Returns("report.csv");
        fixture.Exporter.Setup(e => e.ExportAsync(It.IsAny<DiskItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Disk full"));
        await fixture.ViewModel.ScanCommand.ExecuteAsync();
        await fixture.ViewModel.ExportCommand.ExecuteAsync();
        Assert.Equal("Disk full", fixture.ViewModel.Status);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.True(fixture.ViewModel.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task CompletionCounters_ComeFromResultRatherThanLastProgress()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.ScanCommand.ExecuteAsync();
        Assert.Equal(1, fixture.ViewModel.FilesScanned);
        Assert.Equal(20, fixture.ViewModel.BytesScanned);
        Assert.Equal(0, fixture.ViewModel.FoldersScanned);
    }

    [Fact]
    public async Task Filter_IsDebouncedAndKeepsAncestorsAndOriginalTotals()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.ScanCommand.ExecuteAsync();
        fixture.ViewModel.SearchText = "absent";
        fixture.ViewModel.SearchText = "file";
        fixture.Time.Advance(TimeSpan.FromMilliseconds(250));
        await fixture.ViewModel.FilterTask;
        Assert.Equal(20, fixture.ViewModel.Results[0].Item.SizeBytes);
        Assert.Single(fixture.ViewModel.Results[0].Children);
    }

    [Fact]
    public async Task FolderLoading_IsAsyncAndLoadedOnlyOnce()
    {
        var catalog = new Mock<IFolderCatalog>();
        var completion = new TaskCompletionSource<IReadOnlyList<FolderEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Setup(c => c.GetChildrenAsync("root", It.IsAny<CancellationToken>())).Returns(completion.Task);
        var item = new FolderTreeItemViewModel(new FolderEntry("root", "root", ""), catalog.Object, _ => { }, default);
        item.IsExpanded = true;
        Assert.False(item.LoadingTask.IsCompleted);
        completion.SetResult([new FolderEntry("child", "child", "")]);
        await item.LoadingTask;
        item.IsExpanded = false;
        item.IsExpanded = true;
        await item.LoadingTask;
        Assert.Equal("child", Assert.Single(item.Children).Name);
        catalog.Verify(c => c.GetChildrenAsync("root", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void LazyRows_DoNotMaterializeDescendantsUntilRequested()
    {
        var root = Fixture.Tree();
        var snapshot = ResultSnapshot.Create(root);
        var visible = snapshot.Filter(null, null, 0, default);
        var vm = new DiskItemViewModel(snapshot.Rows[root.FullPath], root.LogicalSizeBytes, snapshot, visible);
        Assert.Empty(vm.LoadedChildren);
        Assert.Single(vm.Children);
        Assert.Single(vm.LoadedChildren);
    }

    private sealed class Fixture : IDisposable
    {
        public Mock<IFileActions> Actions { get; } = new();
        public Mock<IUserInteraction> Interaction { get; } = new();
        public Mock<IDiskUsageExporter> Exporter { get; } = new();
        public Mock<ICsvExportPicker> ExportPicker { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public MainWindowViewModel ViewModel { get; }
        public static DiskItem Tree()
        {
            var path = Path.Combine(Path.GetTempPath(), "dua-vm");
            var root = new DiskItem { Name = "root", FullPath = path, ItemType = DiskItemType.Directory };
            root.Children.Add(new DiskItem { Name = "file.bin", FullPath = Path.Combine(path, "file.bin"), ItemType = DiskItemType.File, LogicalSizeBytes = 20 });
            DiskTree.Recalculate(root);
            return root;
        }
        public Fixture()
        {
            var scanner = new Mock<IDiskScanner>();
            scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Tree);
            var session = new ScanSessionCoordinator(scanner.Object, Exporter.Object, Mock.Of<IFileSystemChangeMonitor>());
            ViewModel = new MainWindowViewModel(session, Mock.Of<IFolderPicker>(), ExportPicker.Object, Mock.Of<IElevatedRelauncher>(),
                Actions.Object, Interaction.Object, Mock.Of<IUiDispatcher>(), Mock.Of<IFolderCatalog>(), Time) { SelectedPath = Tree().FullPath };
        }
        public void Dispose() => ViewModel.Dispose();
    }
}
