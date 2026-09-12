using System.Globalization;
using System.Text;
using System.Text.Json;
using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Infrastructure.Export;

public sealed class CsvDiskUsageExporter : IDiskUsageExporter
{
    public async Task ExportAsync(DiskItem root, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputPath);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                await writer.WriteLineAsync("Path,Name,Type,LogicalSizeBytes,FileCount,FolderCount,LastModified,IsReparsePoint,ErrorCount,ErrorDetails".AsMemory(), cancellationToken);
                foreach (var item in DiskTree.Enumerate(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new[] { item.FullPath, item.Name, item.ItemType.ToString(),
                        item.LogicalSizeBytes.ToString(CultureInfo.InvariantCulture), item.FileCount.ToString(CultureInfo.InvariantCulture),
                        item.FolderCount.ToString(CultureInfo.InvariantCulture), item.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                        item.IsReparsePoint.ToString(), item.SubtreeErrorCount.ToString(CultureInfo.InvariantCulture),
                        item.Errors.Count == 0 ? "" : JsonSerializer.Serialize(item.Errors) };
                    await writer.WriteLineAsync(string.Join(",", values.Select(Escape)).AsMemory(), cancellationToken);
                }
                await writer.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
