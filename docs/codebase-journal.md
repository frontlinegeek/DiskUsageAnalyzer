# Codebase Journal

## Purpose and inventory

Disk Usage Analyzer is a Windows .NET 10 WPF desktop utility for inspecting logical file sizes without reading file contents. The active solution contains Core (models, filtering, formatting, incremental updates), Infrastructure (filesystem scanning, snapshots, monitoring, CSV), App (WPF views, commands, folder navigation, operation coordination), and xUnit tests. The separate `Disk Usage Analyzer/` directory is an unused WPF starter project, preserved intentionally.

The workflow is folder selection -> background metadata scan -> size-sorted result tree -> filtering and optional live updates -> CSV export or shell actions. Elevation is optional. Deletion will require session opt-in and confirmation.

## Baseline review

- Release build: zero warnings/errors on SDK 10.0.401.
- Existing tests: 15 reported passing. Combined Core/Infrastructure line coverage 56.27%; Core 80.61%, Infrastructure 37.79%. WPF was not covered.
- Created-file deltas omit the immediate parent, including root-level totals. Unknown parents can misplace files. Repeated recursive searches make event batches expensive.
- Watcher startup is blocked by scan state; stopping during refresh loses events. Continuous changes postpone debounce indefinitely. Refresh/export are not coordinated with other operations.
- Scanner recursion risks stack exhaustion; metadata exceptions are inconsistently recorded; ancestor error copies waste memory and become stale.
- Filtering clones every domain node and eagerly creates all view models on the UI thread. Folder expansion performs blocking I/O.
- Export lacks transactional replacement and command error handling. Deletion is exposed by default despite the read-only specification.
- Existing link test silently returns when link creation fails; progress assertions depend on asynchronous callback timing.

## Implementation log

Completed the approved stabilization pass. No Git metadata is present in this workspace; no repository was initialized and the unused starter project was preserved.

- Added regression tests first: root-level creation, nested-parent creation, and unknown-parent insertion all failed against the original implementation.
- Replaced recursive scanning with iterative enumeration and reverse-order aggregation. Each directory enumerator is disposed before children are scanned. Injected metadata allows deterministic denied-access, disappearing-entry, deep-tree, cancellation, and reparse tests.
- Store errors once on affected items and aggregate SubtreeErrorCount. Unsupported link traversal/allocation requests fail explicitly. Inaccessible roots return an incomplete result.
- Reworked delta batches around a single path index and dirty ancestors, with exact-parent checks, inclusion-aware snapshot outcomes, rename reconciliation, ordering, and consistent totals even after a partially failed batch.
- Added session coordination, cancellable async commands, stale-progress protection, queued watcher events, bounded debounce, generation checks, and shutdown cancellation. Canceled root switches restore the previous committed scan. Refresh failures retain reconciliation work without a retry loop.
- Replaced per-keystroke domain cloning and eager view-model construction with immutable display snapshots, background debounced matching, and lazy children. Folder browsing is asynchronous. Narrow layouts wrap toolbar/status content, scroll the result columns horizontally, and preserve aligned numeric columns at deeper levels.
- Kept deletion behind nonpersistent session opt-in and confirmation. Membership, type, root containment, and reparse ancestry are revalidated. Deletion is reflected in the result even if its follow-up scan fails.
- Added atomic CSV destination replacement, invariant formatting, local error details, exception reporting, and preservation of an existing destination when export fails or is canceled.
- Added a separate Windows application xUnit/Moq project, optional STA WPF smoke test, and opt-in diagnostic console project. Moq is the only new package dependency.

### Theme support — 2026-09-15

- Added Light, Dark, and System theme preferences through UI-owned resource dictionaries, keeping Core and Infrastructure independent of WPF.
- System mode reads the Windows app-color preference and reapplies the palette and title-bar treatment when Windows broadcasts a settings change.
- The selected preference is stored in a portable `settings.json` beside the executable. Missing, malformed, or read-only settings fall back safely to System without blocking startup.
- Replaced hard-coded main-window colors with semantic brushes and added themed control, selection, warning, error, divider, and usage-bar resources.
- Extended the opt-in WPF smoke workflow to switch themes, verify the effective brushes, and render a Dark-mode artifact. Added direct settings round-trip and malformed-file tests.

## Verification

- Final Release run: 72 passed, 1 explicitly skipped (real symbolic-link creation requires an unavailable privilege). This includes real junction traversal protection and the WPF workflow.
- Debug run: 71 passed, 2 skipped (symbolic-link capability and opt-in WPF smoke). Debug and Release compile without warnings/errors.
- The WPF smoke test exercises scan, cancel with prior-result preservation, filter, live file creation, CSV export, and rendering at 1240x760 and 800x600. Desktop/narrow PNGs were visually inspected under `artifacts/ui/`.
- A real symbolic-link test was attempted explicitly; Windows reported that the client lacks the required privilege. It remains an explicit opt-in skip. A real directory-junction test passes without that privilege. Its initial recursive cleanup exposed a Windows junction-removal quirk; cleanup now removes the generated junction nonrecursively before removing its fixture. The leftover empty fixture was removed.
- Coverage merged by executable source line across the two Release reports: Core 194/211 (91.9%), Infrastructure 211/219 (96.3%), App 518/671 (77.2%). WPF markup itself is not included in C# line coverage. Shell launching, UAC, and successful physical deletion are intentionally not exercised against user data.
- Test artifacts are generated under each test project's TestResults directory and are ignored by Git patterns. Generated filesystem fixtures clean up after themselves.

## Efficiency measurements

One warmed metadata scan of the same generated fixture: 100 directories, 10,000 files of 1,024 logical bytes each. A batch then changes 1,000 file sizes through an in-memory snapshot provider. File creation and fixture cleanup are outside measured scan/update time.

| Measurement | Original | Final sample |
| --- | ---: | ---: |
| Scan | 23.65 ms | 22.71 ms |
| Scan managed allocations | 5,515,336 B | 6,098,616 B |
| 1,000 incremental updates | 279.29 ms | 20.23 ms |
| Update managed allocations | 82,217,048 B | 1,621,216 B |
| Process peak working set | 66,899,968 B | 65,970,176 B |
| Display snapshot creation | Not measured | 8.48 ms |
| Filter request (10-run average) | Not measured | 11.67 ms |

An earlier updated sample measured 9.11 ms for the same update batch; timing varies with runtime warmup and concurrent machine activity. The final sample is about 14 times faster for this batch with about 98% fewer managed allocations. Scan speed is effectively unchanged; scan allocations increased about 11% for richer metadata/error handling. Filtering now runs off the UI thread, but its old implementation was not timed, so no filter speedup claim is made. These are local diagnostic samples, not whole-drive or cold-cache benchmarks.

## Remaining boundaries

- FileSystemWatcher is eventually consistent; buffers and uncertain directory changes require reconciliation. Access errors still mean totals can be incomplete.
- Permanent deletion remains nontransactional against concurrent external filesystem changes. The opt-in, confirmation, and path checks do not promise transactional isolation or undo.
- Production logging still uses Trace without the planned rolling-file deployment setup. Window/layout settings and portable publish verification belong to the deferred milestones below.
- Very large-tree behavior is covered by deterministic deep-tree tests and the 10,000-file diagnostic; million-file production profiling is still useful before adding native scanning or persistent indexes.

## Deferred roadmap

1. Structured rolling-file logging and user-facing error browsing.
2. Portable win-x64 self-contained publish profile and deployment smoke tests.
3. Persist window position, size, units, filters, and other non-theme preferences in the portable settings file.
4. Allocated sizes, additional visualizations, and scan comparisons only after profiling and explicit feature design.
