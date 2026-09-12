namespace DiskUsageAnalyzer.App.Services;

public interface IUiDispatcher { void Post(Action action); }

public sealed class UiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }
}
