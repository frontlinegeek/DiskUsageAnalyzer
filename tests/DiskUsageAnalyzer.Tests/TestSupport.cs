namespace DiskUsageAnalyzer.Tests;

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class LinkFactAttribute : FactAttribute
{
    public LinkFactAttribute()
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("DUA_LINK_TESTS") != "1")
            Skip = "Set DUA_LINK_TESTS=1 on a host with symbolic-link permission. Deterministic link coverage always runs.";
    }
}

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly List<ManualTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _now.Ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan duration)
    {
        _now += duration;
        foreach (var timer in _timers.ToArray()) timer.Fire();
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset _due = DateTimeOffset.MaxValue;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
            return true;
        }
        public void Fire()
        {
            if (_disposed || _due > owner._now) return;
            _due = DateTimeOffset.MaxValue;
            callback(state);
        }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
