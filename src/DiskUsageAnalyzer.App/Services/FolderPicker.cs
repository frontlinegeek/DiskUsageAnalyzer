using System.Windows.Forms;
using System.IO;

namespace DiskUsageAnalyzer.App.Services;

public interface IFolderPicker
{
    string? PickFolder(string? initialPath);
}

public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialPath)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder or drive to scan",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
