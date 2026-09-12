using DiskUsageAnalyzer.Core.Models;

namespace DiskUsageAnalyzer.Core.Updating;

public enum SnapshotStatus { Available, Missing, Excluded, Inaccessible }

public sealed record SnapshotResult(SnapshotStatus Status, DiskItem? Item = null, ScanError? Error = null);
