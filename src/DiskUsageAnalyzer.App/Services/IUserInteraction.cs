using System.Windows;

namespace DiskUsageAnalyzer.App.Services;

public interface IUserInteraction
{
    bool ConfirmDeletion(string path);
    void Shutdown();
}

public sealed class UserInteraction : IUserInteraction
{
    public bool ConfirmDeletion(string path) => System.Windows.MessageBox.Show($"Permanently delete this item and its contents?\n\n{path}",
        "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public void Shutdown() => System.Windows.Application.Current.Shutdown();
}
