# Disk Usage Analyzer

Windows-focused .NET 10 desktop disk usage analyzer.

## Project Layout

- `src/DiskUsageAnalyzer.App`: WPF UI and app-layer view models.
- `src/DiskUsageAnalyzer.Core`: UI-independent domain models, scan contracts, formatting, and filtering.
- `src/DiskUsageAnalyzer.Infrastructure`: file-system scanner, SQLite cache, NTFS journal, and CSV export implementation.
- `tests/DiskUsageAnalyzer.Tests`: unit and integration-style scanner tests using temporary folders.

The scanner reads metadata only. Reparse points are marked but never traversed. Logical and allocated sizes are retained where Windows can report both values, along with file attributes and NTFS file IDs.

Completed scans are stored in `%LOCALAPPDATA%\DiskUsageAnalyzer\scan-cache.db`. Selecting a previously scanned root or using **Load cache** displays its last completed result and timestamp. **Incremental refresh** validates the volume serial and NTFS USN journal checkpoint, applies creates, deletes, renames, moves, and metadata changes, and recalculates directory totals. It automatically performs a full scan if the journal is unavailable, replaced, wrapped, or cannot be reconciled safely. **Full scan** always rebuilds the selected root, and **Delete cache** removes its persisted scan without deleting filesystem content.

SQLite replacement and incremental updates run in transactions. A canceled or failed write leaves the prior completed cache intact. The cache stores individual entries and errors, has parent/path/file-ID indexes, supports multiple roots, and exposes child queries for future fully virtualized result views.

Deletion is disabled on every launch. Enabling deletion permits confirmed permanent removal of eligible descendants of the current scan root. Drives, the scan root, reparse points, and paths reached through reparse points are rejected. There is no recycle-bin integration. Filesystem permissions and concurrent external changes can still cause an operation to fail.

Live monitoring is optional. File changes are applied incrementally; uncertain directory changes and watcher overflow trigger reconciliation scans. Errors appear in totals and mark results incomplete. CSV exports include the complete committed scan, regardless of display filters, and contain local error details.

The interface supports Light, Dark, and System themes. The selected preference is saved in `settings.json` beside the executable; System follows Windows app-color changes while the application is running.

## Development

Build:

```powershell
dotnet build "Disk Usage Analyzer.sln"
```

Test:

```powershell
dotnet test "Disk Usage Analyzer.sln"
```

Run the WPF app:

```powershell
dotnet run --project src\DiskUsageAnalyzer.App\DiskUsageAnalyzer.App.csproj
```

## Windows Installer

The installer is a self-contained x64 package for Windows 10 and 11. It installs
for the current user, creates a Start menu shortcut, and does not require the
.NET runtime or administrator rights.

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```powershell
.\installer\Build-Installer.cmd -Version 1.1.0
```

The finished setup executable is written to `artifacts\installer`. To use an
Inno Setup compiler outside its standard location, pass its full path with
`-InnoCompiler` or set `INNO_SETUP_COMPILER`.

## Tests and Diagnostics

The solution contains xUnit/Moq suites for Core/Infrastructure and Windows application behavior. Filesystem integration tests use isolated temporary folders. Cache tests cover persistence, multiple roots, rollback, journal fallback, rename/move/delete reconciliation, and aggregate-size propagation. Deletion decisions use mocks. WPF rendering tests are opt-in:

```powershell
$env:DUA_UI_SMOKE = '1'
dotnet test "Disk Usage Analyzer.sln" -c Release --collect "XPlat Code Coverage"
```

The rendering workflow exercises scanning, cancellation, filtering, live updates, export, and desktop/narrow layouts. PNGs are saved under the test output's `TestResults/ui`, or the directory specified by `DUA_ARTIFACTS`.

Real junction tests run on Windows. The symbolic-link integration test requires Developer Mode or the corresponding privilege; set `DUA_LINK_TESTS=1` only on a capable host. Otherwise it is explicitly skipped, while deterministic reparse tests still execute.

Run the opt-in generated 10,000-file diagnostic fixture:

```powershell
dotnet run --project diagnostics/DiskUsageAnalyzer.Diagnostics -c Release
```

This reports warmed scan time, throughput, allocations, incremental-update time, result projection/filter time, and process peak working set. Results depend on filesystem caches and the machine; they are not pass/fail performance gates.

See [architecture](docs/architecture.md) and the [codebase journal](docs/codebase-journal.md) for design decisions, measured changes, and the prioritized backlog. The old `Disk Usage Analyzer/` starter project is intentionally preserved but is not part of the active solution. Settings persistence, production file logging, and portable publishing remain follow-up work.
