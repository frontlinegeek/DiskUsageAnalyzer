# Architecture

## Responsibilities

| Subsystem | Responsibility |
| --- | --- |
| Core models and DiskTree | Disk items, local scan errors, subtree error counts, iterative traversal, aggregate totals and ordering |
| Core scanning contracts | IDiskScanner, scan options/progress, injectable metadata entries |
| Core filtering/formatting | Name/path/extension/minimum-size rules and binary size formatting |
| Core updating | Indexed per-batch deltas, snapshot outcome states, bounded change accumulation, monitor contracts |
| Infrastructure scanning | Standard .NET metadata access, iterative directory enumeration, error isolation and throttled progress |
| Infrastructure updating/watching | Inclusion-aware snapshots, FileSystemWatcher event adapter, timed batches with generation checks |
| Infrastructure export | Complete-scan CSV with local error details and transactional destination replacement |
| App session coordinator | Serial scan/refresh/export/delete operations, cancellation, watcher sessions, reconciliation and result publication |
| App projections/view models | Immutable display rows per committed revision, background filtering and lazy child view models |
| App services/commands | Async commands, folder navigation, dialogs, shell/clipboard actions, deletion checks, elevation and dispatch |
| Tests | xUnit/Moq unit and regression tests, temporary filesystem integration tests, optional STA WPF smoke workflow |
| Diagnostics | Opt-in generated fixture measuring scanning, batches, projection, filtering and memory |

## Data flow and ownership

The scanner returns a full logical-size tree. Recoverable errors belong only to the affected item; SubtreeErrorCount is an aggregate. Directory totals exclude the directory itself. Progress folders include the root while scanning; completion counters use the committed root's descendant counts.

ScanSessionCoordinator owns the mutable domain tree behind an asynchronous operation gate. The UI only reads a ResultSnapshot containing immutable row values and child path arrays. A projection is created once per committed revision. Filtering uses those rows on a worker thread, preserving original aggregate sizes and ancestors of matches. Only requested child view models are materialized.

Changes are buffered while scans, exports, deletion, or other refreshes run. The watcher subscribes before scanning, flushes after 900 ms of inactivity or two seconds of sustained activity, and collapses more than 10,000 pending events into root reconciliation. Session/generation checks reject old callbacks. Refresh errors retain reconciliation work without scheduling a retry loop. New changes or an explicit scan can recover.

The delta updater builds a case-insensitive path index once per batch and recalculates dirty ancestors deepest-first. Missing/excluded snapshots remove entries; inaccessible snapshots retain the known item, record an error, and request reconciliation. Unknown parents are rescanned instead of receiving misplaced files. Directory renames preserve known structure and trigger metadata reconciliation.

## Failure and cancellation behavior

Canceled full scans do not publish partial scan results; an existing completed scan remains available. Late progress callbacks are ignored after scan completion. Shutdown cancels outstanding work and stops monitoring. Incremental failures preserve internally consistent applied changes, publish an incomplete indicator, and queue reconciliation.

CSV export serializes access to the current tree and writes to a unique sibling temporary file. The previous destination is replaced only after the writer closes successfully. Failure/cancellation removes the temporary output and preserves an existing destination.

Deletion is a session opt-in followed by confirmation. The coordinator rechecks membership; the filesystem service rechecks containment, type, and all path ancestors for reparse points before removing anything. A completed deletion is reflected in the model even if the subsequent rescan fails. These are convenience safeguards, not a filesystem transaction against concurrent external modification.

## Platform and dependencies

WPF, Windows shell actions, UAC elevation, and filesystem watching live outside Core. Core and Infrastructure target net10.0; App and its tests target net10.0-windows. Moq is used only by tests. No UI framework migration, native scanning implementation, or DI container was added.

The unused starter project is not loaded by the solution. Production structured file logging, settings persistence, and a portable self-contained publishing profile remain separate follow-up milestones.
