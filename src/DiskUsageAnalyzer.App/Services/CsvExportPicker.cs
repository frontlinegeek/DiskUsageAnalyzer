namespace DiskUsageAnalyzer.App.Services;

public interface ICsvExportPicker
{
    string? PickOutputPath(string suggestedFileName);
}

public sealed class CsvExportPicker : ICsvExportPicker
{
    public string? PickOutputPath(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".csv",
            FileName = suggestedFileName,
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            OverwritePrompt = true,
            Title = "Export scan results"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
