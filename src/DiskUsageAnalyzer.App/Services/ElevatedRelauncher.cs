using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace DiskUsageAnalyzer.App.Services;

public interface IElevatedRelauncher
{
    Process? Relaunch(string? selectedPath);
}

public sealed class ElevatedRelauncher : IElevatedRelauncher
{
    public Process? Relaunch(string? selectedPath)
    {
        var startInfo = CreateStartInfo();
        AddSelectedPathArgument(startInfo, selectedPath);
        return Process.Start(startInfo);
    }

    internal static void AddSelectedPathArgument(ProcessStartInfo startInfo, string? selectedPath)
    {
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            startInfo.ArgumentList.Add(selectedPath);
        }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Current process path is not available.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            Verb = "runas"
        };

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                throw new InvalidOperationException("Entry assembly path is not available.");
            }

            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        return startInfo;
    }
}
