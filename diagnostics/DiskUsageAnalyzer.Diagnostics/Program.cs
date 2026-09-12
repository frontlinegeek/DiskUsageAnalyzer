using System.Diagnostics;
using System.Text.Json;
using DiskUsageAnalyzer.Core.Models;
using DiskUsageAnalyzer.Core.Scanning;
using DiskUsageAnalyzer.Core.Updating;
using DiskUsageAnalyzer.Infrastructure.Scanning;
using DiskUsageAnalyzer.App.Services;

var fixture = Path.Combine(Path.GetTempPath(), "dua-diagnostic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    for (var folder = 0; folder < 100; folder++)
    {
        var path = Directory.CreateDirectory(Path.Combine(fixture, folder.ToString())).FullName;
        for (var file = 0; file < 100; file++)
        {
            using var stream = File.Create(Path.Combine(path, file + ".bin"));
            stream.SetLength(1024);
        }
    }
    var scanner = new FileSystemDiskScanner();
    var options = new ScanOptions { RootPath = fixture, IncludeHiddenItems = true, IncludeSystemItems = true };
    await scanner.ScanAsync(options, null, CancellationToken.None);
    var allocation = GC.GetTotalAllocatedBytes(true);
    var elapsed = Stopwatch.StartNew();
    var root = await scanner.ScanAsync(options, null, CancellationToken.None);
    var scanMs = elapsed.Elapsed.TotalMilliseconds;
    var scanAllocation = GC.GetTotalAllocatedBytes(true) - allocation;
    var changes = root.Children.SelectMany(item => item.Children).Take(1000)
        .Select(item => new DiskUsageChange { Kind = DiskUsageChangeKind.Changed, FullPath = item.FullPath }).ToArray();
    allocation = GC.GetTotalAllocatedBytes(true);
    elapsed.Restart();
    new DiskUsageDeltaUpdater().Apply(root, changes, new Snapshots());
    var deltaMs = elapsed.Elapsed.TotalMilliseconds;
    var deltaAllocation = GC.GetTotalAllocatedBytes(true) - allocation;
    elapsed.Restart();
    var snapshot = ResultSnapshot.Create(root);
    var projectionMs = elapsed.Elapsed.TotalMilliseconds;
    allocation = GC.GetTotalAllocatedBytes(true);
    elapsed.Restart();
    for (var i = 0; i < 10; i++) snapshot.Filter("1", "bin", 0, default);
    var filterMs = elapsed.Elapsed.TotalMilliseconds / 10;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Files = root.FileCount, Folders = root.FolderCount, ScanMs = scanMs,
        ItemsPerSecond = 10101 / (scanMs / 1000), ScanAllocatedBytes = scanAllocation,
        DeltaMs = deltaMs, DeltaAllocatedBytes = deltaAllocation,
        ProjectionMs = projectionMs, FilterMs = filterMs, FilterAllocatedBytes = (GC.GetTotalAllocatedBytes(true) - allocation) / 10,
        PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    var fullPath = Path.GetFullPath(fixture);
    if (Path.GetDirectoryName(fullPath) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
        && Path.GetFileName(fullPath).StartsWith("dua-diagnostic-", StringComparison.Ordinal))
        Directory.Delete(fullPath, true);
}

sealed class Snapshots : IDiskItemSnapshotProvider
{
    public bool TryGetSnapshot(string fullPath, out DiskItem item)
    {
        item = new DiskItem { Name = Path.GetFileName(fullPath), FullPath = fullPath,
            ItemType = DiskItemType.File, LogicalSizeBytes = 2048 };
        return true;
    }
}
