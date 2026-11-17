# WindowsFS architecture

WindowsFS is a native Windows desktop application. The first release answers a focused question: which accessible files and directories account for the most logical bytes in a selected scan scope?

## Boundaries

`WindowsFS.Core` contains the scan contract, directory traversal, aggregation, result models, and snapshot persistence contract. It has no dependency on WPF. `WindowsFS.App` owns presentation, drive selection, navigation, UI dispatch, the user-specific cache location, and Explorer integration. The UI consumes `IDiskScanner` and `IScanResultStore`, allowing later providers to replace enumeration or storage without coupling those services to controls.

New analysis capabilities should use explicit service contracts. A future physical-allocation scanner, duplicate detector, or file-operation service should declare its own semantics and permissions. The first release has no dynamic plugin loading or arbitrary third-party code execution. Extensibility here means separate components and replaceable interfaces.

## Space composition views

`SpaceMap` draws either blocks or a donut using the same bounded `MapItem` collection and palette. Switching the layout changes presentation only: it does not scan, rebuild aggregates, or write a snapshot. The chosen layout stays active during folder and page navigation for the current session. Both layouts support folder activation and keyboard selection. Donut hit testing follows the ring sectors rather than their rectangular bounds, so its center and empty surroundings do not open folders. Small and grouped entries retain their represented bytes.

## System item explanations

`SystemItemCatalog` is an in-memory catalog of source-backed explanations for known Windows paths. It matches an exact standard location and the item kind; it does not inherit a classification from a parent folder or classify a file by basename alone. The catalog does not read executable contents, inspect personal documents, query remote services, or infer an item's actual creator. Its management attribution describes the usual Windows component responsible for that standard location, with an explicit path-based match note and a primary Microsoft reference.

The view model resolves the selected row, or the current known folder when no row is selected. Unknown user files and extension aggregates clear the explanation. The native panel shows purpose, management, necessity, and cleanup guidance; an explicit user action opens the static official reference. Neither catalog lookup nor panel display deletes files or changes system settings. Synthetic tests cover unknown descendants, user copies of system filenames, folder navigation, panel state, and actual XAML bindings.

## Enumeration and aggregation

The scanner normalizes input roots and removes duplicates and nested roots before starting. A bounded pool of background workers enumerates directories using `FileSystemEnumerable`, reading the metadata returned by the operating system. It does not open file contents or recursively create a `FileInfo` object for every retained result.

Windows filesystem access always uses an extended-length path while results retain readable paths. This preserves literal NTFS names ending in periods or spaces: ordinary Win32 normalization can otherwise interpret a directory named `. ` or `...` as its parent and cause repeated traversal. Root normalization resolves actual `.` and `..` navigation segments without discarding meaningful filename characters. Regression tests require these trees to finish naturally with exact counts.

Each worker maintains a bounded collection of large files. These collections are merged to return the largest 1,000 files by default. Folder nodes and extension aggregates are retained, while the full population of file records is not. Retained memory therefore grows with directory count, extension count, pending directory work, and `worker count × largest-file limit`, rather than with every scanned file path. A drive containing millions of directories can still require significant memory.

Directory byte totals include descendants. Reported file sizes are logical lengths, not physical allocation. Hard links are counted once for each visible path. Overlapping selected roots are deduplicated; this is different from hard-link deduplication.

UI updates are throttled to avoid dispatching an event for every file. Cancellation is cooperative. The scanner returns an accumulated snapshot marked `IsCancelled`, so the UI can show useful partial results with an explicit incomplete status. A filesystem enumeration call already in progress must return before that worker can observe cancellation.

## Inaccessible and special entries

The scanner skips reparse points and offline entries, including roots with those attributes. It does not follow directory junctions or symbolic links, preventing traversal loops and crossing into linked scopes. It does not intentionally hydrate cloud placeholders. Protected, removed, or otherwise inaccessible entries contribute to a skipped count. Skipping a directory does not imply knowing how many unreadable descendants it contained.

The app uses ordinary filesystem permissions. It does not request elevation, read raw disks, parse the NTFS MFT, or bypass ACLs. Enumeration works across supported local filesystems without an NTFS-only fast path. The scan is not an atomic volume snapshot: concurrent file changes may affect observations.

## Privacy and persistence

The application automatically persists its latest completed scan to `%LOCALAPPDATA%\WindowsFS\Cache\latest.scan.json.gz`. This location is outside the repository and uses the current Windows user's local application-data directory, rather than the roaming profile. The application has no account service, telemetry endpoint, or cache upload/synchronization service. Read-only scanning does not alter the scanned files. Opening a selected location delegates to Windows Explorer; copying a selected path explicitly sends that path to the Windows clipboard. A user-selected network or UNC folder is accessed through the operating system's filesystem APIs, not an application telemetry service.

The versioned snapshot contains the scan target, timestamp, folder aggregates, bounded largest-file list, extension summaries, and statistics. Folder records are stored in a flat representation and reconstructed iteratively so deeply nested directory trees do not rely on recursive JSON serialization. Serialization and loading run away from the UI thread, with a 512 MiB limit on expanded JSON in both directions. A completed temporary file in the same cache directory atomically replaces the previous snapshot; failed or interrupted saves leave the existing snapshot intact. Normal window closing waits for an in-flight save to finish. Cancellation results remain navigable in memory but do not replace the last complete snapshot. Only one complete snapshot is retained, including when a new scan uses a different target.

Normal startup restores the saved snapshot without enumerating its file tree again. The UI identifies it as saved data and displays the analysis time; the result does not claim to reflect subsequent filesystem changes. A user can explicitly run a new scan to refresh it. Corrupt or incompatible snapshots are reported without crashing startup or preventing a fresh scan. Explicit `--scan` startup arguments request a new scan.

Snapshot validation and navigation indexes preserve the exact casing of enumerated paths. Windows directories can enable case-sensitive names, so distinct siblings such as `Case` and `case` must remain separate when saving, restoring, and navigating. Exactly duplicated paths are still rejected as invalid snapshot data.

Compression is not encryption. The snapshot contains potentially personal file names and paths and relies on the Windows user directory's filesystem permissions for access control. It must not be included in source commits, public screenshots, issue attachments, or distributable application packages. Tests inject temporary or in-memory stores and synthetic snapshots instead of using a real user's cache.

Repository hygiene is a separate layer. `.gitignore` excludes generated output, local SDKs, developer state, common credential material, logs, database files, compressed scan snapshots, and dedicated private-data/export directories. It does not inspect source content or untrack existing files. Review the actual staged diff before publishing. Public documentation and test fixtures must use synthetic paths and generated files.

## Validation and distribution

The core test executable exercises scanner behavior using generated fixtures. A separate Windows test executable checks the WPF view model using synthetic input. CI builds the native WPF project, runs both executables, and publishes a self-contained `win-x64` directory. The artifact contains application and runtime binaries; copying only the main executable is insufficient. Local publish output lives under ignored `.artifacts/publish/`.

The core test executable's optional `--benchmark` argument measures generated fixtures and cleans up its temporary data. Both the first scan and the repeated scan can benefit from the OS metadata cache, especially because the fixtures have just been created. These results are useful for regression comparisons on the same machine, not as a cold-cache or full-drive performance guarantee.

Release binaries are not code signed by this repository. Distribution signing and an installer can be added later without changing the scan contract. Performance claims should include device type, tree shape, file/directory counts, cache state, and measured elapsed time; the project does not claim a universal throughput or an MFT-level scan speed.
