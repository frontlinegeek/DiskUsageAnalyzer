# Disk Usage Analyzer — Agent Instructions and Project Specification

## Purpose

Build a fast, portable disk usage analyzer for Windows using .NET 10.

The application should scan local drives and folders, calculate disk usage efficiently, and present results in a way that makes it easy to identify large folders, large files, wasted space, and unusual storage growth.

This application exists because many existing disk usage tools are either slow, incomplete, visually dated, or missing practical features needed for real-world troubleshooting.

## Primary Goals

- Fast scanning of large folder trees.
- Portable deployment.
- No installer required for the core app.
- Clear visual breakdown of disk usage.
- Search and filtering of scan results.
- Ability to rescan quickly.
- Safe read-only operation by default.
- Good handling of access-denied folders.
- Good handling of junctions, symlinks, reparse points, and system folders.
- Modern, maintainable .NET architecture.
- Designed so future features can be added without major rewrites.

## Target Platform

- Primary platform: Windows 10 and Windows 11.
- Runtime: .NET 10.
- IDE: JetBrains Rider.
- Architecture: x64 first.
- Deployment: portable self-contained build preferred.
- Admin rights: optional, not required for normal operation.

## Application Type

Preferred initial application type:

- Desktop GUI application.
- Native Windows feel is preferred.
- The app should be responsive during scans.
- Scanning must run in background tasks and never block the UI thread.

Candidate UI frameworks:

- Avalonia UI if cross-platform portability is desired later.
- WPF if Windows-only simplicity and maturity are preferred.
- WinUI should only be used if deployment complexity is acceptable.

Decision for initial version: Windows-focused desktop app, but the core scanning engine should not depend on the UI framework.

## High-Level Architecture

The solution should be split into logical projects:

```text
DiskUsageAnalyzer/
  src/
    DiskUsageAnalyzer.App/
    DiskUsageAnalyzer.Core/
    DiskUsageAnalyzer.Infrastructure/
    DiskUsageAnalyzer.Tests/
  docs/
  README.md
  AGENTS.md
```

### DiskUsageAnalyzer.App

Responsible for:

- UI.
- User interaction.
- Scan setup.
- Progress display.
- Result visualization.
- Commands such as scan, cancel, refresh, export, open folder, copy path.

The UI project must not contain core scanning logic.

### DiskUsageAnalyzer.Core

Responsible for:

- Domain models.
- Scan orchestration interfaces.
- Disk usage calculations.
- Filtering and sorting rules.
- Result tree models.
- Cancellation support.
- Error reporting models.

This project should be UI-independent.

### DiskUsageAnalyzer.Infrastructure

Responsible for:

- File system access.
- Windows-specific file metadata.
- Drive enumeration.
- Optional native API integration.
- Export implementations.
- Configuration persistence.

This project may contain platform-specific code.

### DiskUsageAnalyzer.Tests

Responsible for:

- Unit tests.
- Integration-style tests using temporary folder trees.
- Edge case coverage for access errors, symbolic links, empty folders, long paths, and cancellation.

## Core Features for Version 1

### 1. Folder and Drive Scanning

The app must allow scanning:

- Entire drives.
- Selected folders.
- Multiple selected root folders eventually, but single-root scanning is acceptable for the first version.

The scanner must collect:

- Folder path.
- Folder name.
- File count.
- Folder count.
- Total logical size.
- Total allocated size if feasible.
- Largest child items.
- Scan errors.
- Last modified date where useful.

The scanner must support cancellation.

### 2. Performance Requirements

The scanner should be designed for speed.

Requirements:

- Use asynchronous/background execution.
- Avoid unnecessary object allocation.
- Avoid loading full file contents.
- Avoid repeated expensive calls where possible.
- Stream results progressively where practical.
- Handle very large directory trees.
- Do not freeze the UI.
- Report progress as folders/files are processed.

Initial implementation can use standard .NET file APIs.

Future optimization may use native Windows APIs if needed.

### 3. Handling Access Denied and Errors

The scanner must not crash when it encounters:

- Access denied.
- File not found.
- Directory not found.
- Path too long.
- Locked files.
- Invalid reparse points.
- Unauthorized system folders.

Errors should be recorded and associated with the relevant path.

The UI should show that scan results may be incomplete when errors occur.

### 4. Reparse Points, Junctions, and Symlinks

The scanner must detect reparse points.

Default behavior:

- Do not follow reparse points automatically.
- Count the reparse point itself if possible.
- Mark the item as a reparse point in the result model.

Future option:

- Allow the user to enable following symlinks/junctions with clear warning about loops and duplicate counting.

### 5. Result Display

The UI should display results in a tree/grid view.

Minimum columns:

- Name.
- Total size.
- Percentage of parent.
- File count.
- Folder count.
- Last modified.
- Error indicator.

Useful display features:

- Sort by size.
- Expand/collapse folders.
- Search by name or path.
- Filter by minimum size.
- Filter by file extension.
- Show largest files.
- Show largest folders.
- Show scan errors.

### 6. Visual Summary

The app should include at least one visual representation of usage.

Acceptable initial options:

- Horizontal proportional bars in the tree/grid.
- Treemap.
- Donut/pie chart for top-level usage.

A simple proportional bar per row is acceptable for version 1.

Treemap can be a later feature.

### 7. File and Folder Actions

The app should provide safe convenience actions:

- Open folder in Explorer.
- Open parent folder.
- Copy full path.
- Copy summary.
- Export results.

Deletion should not be included in the first version unless explicitly added later.

The app should remain read-only by default.

### 8. Export

Version 1 should support exporting scan results to:

- CSV.

Future options:

- JSON.
- HTML report.
- SQLite cache/database.
- Excel-compatible `.xlsx`.

### 9. Settings

The app should persist simple settings:

- Last scanned path.
- Window size and position.
- Preferred size unit.
- Whether hidden/system files are shown.
- Whether errors are shown.
- Default minimum size filter.
- Theme preference if supported.

Settings should be stored locally beside the app or under the user profile, depending on portability decision.

For true portability, prefer an optional `settings.json` beside the executable.

## Important Design Rules

### Keep Core Logic UI-Independent

The scanning engine must not depend on WPF, Avalonia, WinUI, or any UI-specific type.

Use interfaces and plain models.

### Make Cancellation First-Class

Every scan must accept a `CancellationToken`.

The scanner must check cancellation frequently.

### Do Not Hide Errors

Do not swallow exceptions silently.

Record recoverable errors in the scan result.

Log unexpected errors.

### Avoid Premature Native Complexity

Start with clean .NET APIs.

Only add native Windows API code after profiling proves it is needed.

### Prefer Clear Models

Avoid passing raw dictionaries or anonymous structures through the app.

Use explicit domain models such as:

```csharp
public sealed class DiskItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required DiskItemType ItemType { get; init; }
    public long LogicalSizeBytes { get; set; }
    public long? AllocatedSizeBytes { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool IsReparsePoint { get; set; }
    public List<DiskItem> Children { get; } = [];
    public List<ScanError> Errors { get; } = [];
}
```

### Logging

Use structured logging.

Preferred logging library:

- Serilog.

Log to:

- Console during development.
- Rolling local file for portable builds.

Logs should include:

- Scan start.
- Scan completion.
- Root path.
- Duration.
- File count.
- Folder count.
- Error count.
- Cancellation.
- Unexpected exceptions.

## Suggested Domain Models

```csharp
public enum DiskItemType
{
    Drive,
    Directory,
    File
}
```

```csharp
public sealed class ScanError
{
    public required string Path { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
}
```

```csharp
public sealed class ScanOptions
{
    public required string RootPath { get; init; }
    public bool IncludeHiddenItems { get; init; }
    public bool IncludeSystemItems { get; init; }
    public bool FollowReparsePoints { get; init; }
    public bool CalculateAllocatedSize { get; init; }
}
```

```csharp
public sealed class ScanProgress
{
    public string? CurrentPath { get; init; }
    public long FilesScanned { get; init; }
    public long FoldersScanned { get; init; }
    public long BytesScanned { get; init; }
    public long ErrorsEncountered { get; init; }
}
```

```csharp
public interface IDiskScanner
{
    Task<DiskItem> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
```

## Scanning Behavior

The scanner should walk the directory tree recursively or iteratively.

An iterative approach is preferred if it avoids stack depth problems on very deep folder structures.

The scanner should:

1. Validate the root path.
2. Create a root `DiskItem`.
3. Enumerate files and folders.
4. Accumulate sizes upward.
5. Record recoverable errors.
6. Respect cancellation.
7. Return partial results if cancellation occurs only if the UI is designed to handle partial results.

## Size Calculations

The app should clearly distinguish:

- Logical size: the normal file length reported by the file system.
- Allocated size: actual disk allocation size, if available.

Initial version may calculate only logical size.

Allocated size can be added later because it may require platform-specific logic.

## UX Requirements

The app should feel fast even while scanning.

The UI should show:

- Current scanning path.
- Files scanned.
- Folders scanned.
- Bytes scanned.
- Error count.
- Elapsed time.
- Cancel button.

After scan completion, show:

- Total size.
- Total files.
- Total folders.
- Total errors.
- Scan duration.

## Non-Goals for Version 1

Do not include these in the first version unless specifically requested:

- File deletion.
- Duplicate file detection.
- Cloud drive analysis.
- Network drive optimization.
- Real-time file system monitoring.
- Automatic cleanup recommendations.
- Registry cleanup.
- Browser cache cleanup.
- Installer.
- Background service.

## Future Features

Potential future additions:

- Treemap visualization.
- Duplicate file detection.
- File type grouping.
- Age-based grouping.
- Largest files view.
- Largest folders view.
- Export to JSON.
- Export to HTML.
- Export to XLSX.
- Compare two scans.
- Save scan history.
- Watch mode.
- Network share scanning.
- Admin elevation prompt.
- Plugin-style analyzers.
- Dark mode.
- Multi-root scan.
- Shell context menu integration.

## Coding Standards

- Use modern C#.
- Use nullable reference types.
- Prefer explicit models over dynamic data.
- Prefer dependency injection where it helps maintainability.
- Avoid over-engineering early.
- Avoid business logic in code-behind or UI event handlers.
- Keep methods small enough to test.
- Make edge cases explicit.
- Add XML comments only where they add real value.
- Use clear names over clever names.

## Testing Requirements

Tests should cover:

- Empty folder.
- Folder with files.
- Nested folders.
- Large fake folder tree.
- Access-denied simulation where practical.
- Reparse point detection where practical.
- Cancellation.
- Sorting by size.
- Filtering by extension.
- Error recording.

Use temporary test folders.

Tests must clean up after themselves.

## Performance Testing

Create a simple benchmark or diagnostic mode later that can scan a generated folder tree and report:

- Items per second.
- MB/GB per second based on metadata read.
- Total scan time.
- Memory usage if practical.

Do not optimize blindly.

Profile before adding complex code.

## Agent Instructions

When modifying this project:

- Preserve the separation between UI, core logic, and infrastructure.
- Do not add UI framework dependencies to `DiskUsageAnalyzer.Core`.
- Do not make the scanner throw for normal file system access problems.
- Do not block the UI thread.
- Do not add deletion or destructive file operations unless explicitly requested.
- Prefer small, reviewable changes.
- Add or update tests when changing scan behavior.
- Keep the app portable.
- Ask before introducing large third-party dependencies.
- Explain any platform-specific code clearly.
- Do not follow symlinks or junctions by default.
- Treat performance as a first-class requirement.
- Treat correctness and safety as more important than flashy UI.

## Initial Milestone Plan

### Milestone 1 — Skeleton

- Create solution structure.
- Add core models.
- Add scanner interface.
- Add basic logging.
- Add basic test project.

### Milestone 2 — Basic Scanner

- Implement logical-size scanning.
- Support cancellation.
- Record access errors.
- Detect reparse points.
- Add unit tests.

### Milestone 3 — Basic UI

- Select folder.
- Start scan.
- Cancel scan.
- Show progress.
- Display result tree/grid.

### Milestone 4 — Filtering and Sorting

- Sort by size.
- Search by path/name.
- Filter by minimum size.
- Filter by extension.

### Milestone 5 — Export

- Export current scan to CSV.
- Include error information in export.

### Milestone 6 — Polish

- Save settings.
- Improve progress display.
- Improve error display.
- Add portable publish profile.
- Add README usage notes.
