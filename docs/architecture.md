# WindowsFS architecture

WindowsFS is a native Windows desktop application. The first release answers a focused question: which accessible files and directories account for the most logical bytes in a selected scan scope?

## Boundaries

`WindowsFS.Core` contains the scan contract, directory traversal, aggregation, and result models. It has no dependency on WPF. `WindowsFS.App` owns presentation, drive selection, navigation, UI dispatch, and Explorer integration. The UI consumes `IDiskScanner`, allowing a later scan provider to replace enumeration without coupling the engine to controls.

New analysis capabilities should use explicit service contracts. A future physical-allocation scanner, duplicate detector, or file-operation service should declare its own semantics and permissions. The first release has no dynamic plugin loading or arbitrary third-party code execution. Extensibility here means separate components and replaceable interfaces.

## Enumeration and aggregation

The scanner normalizes input roots and removes duplicates and nested roots before starting. A bounded pool of background workers enumerates directories using `FileSystemEnumerable`, reading the metadata returned by the operating system. It does not open file contents or recursively create a `FileInfo` object for every retained result.

Each worker maintains a bounded collection of large files. These collections are merged to return the largest 1,000 files by default. Folder nodes and extension aggregates are retained, while the full population of file records is not. Retained memory therefore grows with directory count, extension count, pending directory work, and `worker count × largest-file limit`, rather than with every scanned file path. A drive containing millions of directories can still require significant memory.

Directory byte totals include descendants. Reported file sizes are logical lengths, not physical allocation. Hard links are counted once for each visible path. Overlapping selected roots are deduplicated; this is different from hard-link deduplication.

UI updates are throttled to avoid dispatching an event for every file. Cancellation is cooperative. The scanner returns an accumulated snapshot marked `IsCancelled`, so the UI can show useful partial results with an explicit incomplete status. A filesystem enumeration call already in progress must return before that worker can observe cancellation.

## Inaccessible and special entries

The scanner skips reparse points and offline entries, including roots with those attributes. It does not follow directory junctions or symbolic links, preventing traversal loops and crossing into linked scopes. It does not intentionally hydrate cloud placeholders. Protected, removed, or otherwise inaccessible entries contribute to a skipped count. Skipping a directory does not imply knowing how many unreadable descendants it contained.

The app uses ordinary filesystem permissions. It does not request elevation, read raw disks, parse the NTFS MFT, or bypass ACLs. Enumeration works across supported local filesystems without an NTFS-only fast path. The scan is not an atomic volume snapshot: concurrent file changes may affect observations.

## Privacy and persistence

The application has no account service, telemetry endpoint, scan cache, or automatic scan export. Paths and aggregates remain in process memory for the active session. Read-only scanning does not alter the scanned files. Opening a selected location delegates to Windows Explorer; copying a selected path explicitly sends that path to the Windows clipboard. A user-selected network or UNC folder is accessed through the operating system's filesystem APIs, not an application telemetry service.

Repository hygiene is a separate layer. `.gitignore` excludes generated output, local SDKs, developer state, common credential material, logs, database files, and dedicated private-data/export directories. It does not inspect source content or untrack existing files. Review the actual staged diff before publishing. Public documentation and test fixtures must use synthetic paths and generated files.

## Validation and distribution

The core test executable exercises scanner behavior using generated fixtures. A separate Windows test executable checks the WPF view model using synthetic input. CI builds the native WPF project, runs both executables, and publishes a self-contained `win-x64` directory. The artifact contains application and runtime binaries; copying only the main executable is insufficient. Local publish output lives under ignored `.artifacts/publish/`.

The core test executable's optional `--benchmark` argument measures generated fixtures and cleans up its temporary data. Both the first scan and the repeated scan can benefit from the OS metadata cache, especially because the fixtures have just been created. These results are useful for regression comparisons on the same machine, not as a cold-cache or full-drive performance guarantee.

Release binaries are not code signed by this repository. Distribution signing and an installer can be added later without changing the scan contract. Performance claims should include device type, tree shape, file/directory counts, cache state, and measured elapsed time; the project does not claim a universal throughput or an MFT-level scan speed.
