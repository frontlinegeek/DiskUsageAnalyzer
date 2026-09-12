using System.IO;

namespace DiskUsageAnalyzer.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dua-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        if (System.IO.Path.GetDirectoryName(resolved) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath())
            || !System.IO.Path.GetFileName(resolved).StartsWith("dua-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary fixture escaped its intended directory.");
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
