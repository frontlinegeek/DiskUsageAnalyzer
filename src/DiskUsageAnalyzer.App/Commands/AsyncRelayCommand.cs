using System.Windows.Input;

namespace DiskUsageAnalyzer.App.Commands;

public sealed class AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null,
    Action<Exception>? failed = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await execute(parameter); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }
    public async void Execute(object? parameter)
    {
        try { await ExecuteAsync(parameter); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Command failed: {0}", ex); failed?.Invoke(ex); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
