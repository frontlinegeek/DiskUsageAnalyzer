using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Infrastructure.Watching;
using Moq;

namespace DiskUsageAnalyzer.Tests.Watching;

public sealed class FileSystemChangeMonitorTests
{
    private static DiskUsageChange Change(int i = 0) => new() { Kind = DiskUsageChangeKind.Changed, FullPath = Path.Combine(Path.GetTempPath(), "dua", i.ToString()) };

    [Fact]
    public void Start_SubscribesBeforeSourceCanEmit()
    {
        var time = new ManualTimeProvider();
        var source = new Mock<IFileSystemEventSource>();
        source.Setup(s => s.Start()).Callback(() => source.Raise(s => s.Changed += null, Change()));
        using var monitor = new FileSystemChangeMonitor(time, _ => source.Object);
        var batches = new List<ChangeBatch>();
        monitor.ChangesReady += batches.Add;
        monitor.Start(Path.GetTempPath(), 7);
        time.Advance(TimeSpan.FromMilliseconds(899));
        Assert.Empty(batches);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(7, Assert.Single(batches).Session);
        Assert.Single(batches[0].Changes);
    }

    [Fact]
    public void ContinuousActivity_FlushesByTwoSeconds()
    {
        var time = new ManualTimeProvider();
        var source = new Mock<IFileSystemEventSource>();
        using var monitor = new FileSystemChangeMonitor(time, _ => source.Object);
        var batches = new List<ChangeBatch>();
        monitor.ChangesReady += batches.Add;
        monitor.Start(Path.GetTempPath(), 1);
        for (var i = 0; i < 4; i++)
        {
            source.Raise(s => s.Changed += null, Change(i));
            time.Advance(TimeSpan.FromMilliseconds(500));
        }
        Assert.Equal(4, Assert.Single(batches).Changes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopOrDispose_RejectsLateCallbacks(bool dispose)
    {
        var time = new ManualTimeProvider();
        var source = new Mock<IFileSystemEventSource>();
        using var monitor = new FileSystemChangeMonitor(time, _ => source.Object);
        var batches = new List<ChangeBatch>();
        monitor.ChangesReady += batches.Add;
        monitor.Start(Path.GetTempPath(), 1);
        source.Raise(s => s.Changed += null, Change());
        if (dispose) monitor.Dispose(); else monitor.Stop();
        source.Raise(s => s.Changed += null, Change());
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(batches);
        Assert.False(monitor.IsWatching);
    }

    [Fact]
    public void Restart_RejectsOldSource()
    {
        var time = new ManualTimeProvider();
        var old = new Mock<IFileSystemEventSource>();
        var current = new Mock<IFileSystemEventSource>();
        var calls = 0;
        using var monitor = new FileSystemChangeMonitor(time, _ => ++calls == 1 ? old.Object : current.Object);
        var batches = new List<ChangeBatch>();
        monitor.ChangesReady += batches.Add;
        monitor.Start(Path.GetTempPath(), 1);
        monitor.Start(Path.GetTempPath(), 2);
        old.Raise(s => s.Changed += null, Change(1));
        current.Raise(s => s.Changed += null, Change(2));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Assert.Single(batches).Session);
        Assert.Equal(Change(2).FullPath, Assert.Single(batches[0].Changes).FullPath);
    }

    [Fact]
    public void EventLimit_CollapsesToSingleOverflow()
    {
        var pending = new ChangeAccumulator(Path.GetTempPath());
        for (var i = 0; i < 11000; i++) pending.Add(Change(i), DateTimeOffset.UnixEpoch);
        Assert.Equal(1, pending.Count);
        Assert.Equal(DiskUsageChangeKind.Overflow, Assert.Single(pending.Drain()).Kind);
        pending.Add(Change(), DateTimeOffset.UnixEpoch);
        Assert.Equal(DiskUsageChangeKind.Changed, Assert.Single(pending.Drain()).Kind);
    }
}
