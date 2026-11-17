# WindowsFS project rules

## Permanent read-only product boundary

The user requires WindowsFS to remain a read-only analyzer of user and system files. This applies to all future versions and extensions, not just the initial release.

- Do not add creation, deletion, recycling, copying, moving, renaming, editing, permission changes, or other mutations of analyzed files and directories.
- Do not expose these operations through buttons, keyboard shortcuts, context menus, drag-and-drop, shell dialogs, external commands, plugins, or background jobs.
- Use the app's read-only folder picker. Native Windows file/folder dialogs expose shell operations such as creating a folder and must not be used for target selection.
- Selecting a supported file automatically previews a bounded portion of its contents using read-only streams. Page navigation may read another page; audio/video playback remains explicit. Never execute content or shell preview handlers, persist preview content, or hydrate offline files.
- Scanning and folder browsing read filesystem metadata only. Never open analyzed file contents for writing.
- Runtime filesystem writes are limited to the app-owned analysis cache: `%LOCALAPPDATA%\WindowsFS\Cache\latest.scan.json.gz`, its containing directory, and uniquely named temporary files used for atomic cache replacement.
- Deleting a saved analysis removes only the configured cache file and in-memory results. Never derive a write/delete target from a scan result, selected row, selected scan root, search text, or snapshot contents. Never recursively delete a cache directory.
- Explorer integration may only reveal a selected path. Clipboard integration may only copy the path text. Official help links may only open documentation; do not execute cleanup commands.
- Cleanup analysis is explanation and read-only browsing only. Group saved folder metadata without rescanning; installed-app registry metadata may be read without write access. Never execute uninstall strings, cleanup tools, scripts, or automatic removal actions. A cleanup candidate is not a guarantee of safe deletion; preserve explicit evidence and unknown classifications.
- Cleanup file lists may read current immediate file metadata only from the selected bucket's saved locations. Keep saved aggregate totals distinct from live lists; show read time and partial results. Do not discover new subtrees or persist these lists. Candidate filtering must also exclude non-candidate buckets from visible totals.

## Navigation

- Keep the main window free of a sidebar. Largest files, folder browsing, and file-type views are reached from dashboard cards, with a visible return to the dashboard.
- Keep page titles aligned independently of the dashboard return control. Folder-path navigation must use the saved result index without rescanning or accessing paths outside the analyzed scope.
- Space composition and details have user-resizable widths, preserved across file/folder selection changes and panel reopening. Reserve usable space for the result list and details when resizing. A system explanation alone must not hide the map; collapse it only when the available layout width is insufficient, and restore the chosen widths when space returns.
- Main-window controls for all local drives, individual disks, and disk-capacity widgets are deferred until the user decides their UI. Do not add replacement drive widgets by inference. Internal scan targets and read-only folder selection remain available.

## Verification and privacy

- Review every new filesystem or shell call against the read-only boundary and the narrow cache exception.
- Test filesystem behavior only with isolated synthetic temporary fixtures. Never test creation/deletion using the user's files or real analysis cache.
- Keep UI text limited to concise functional descriptions and statuses; do not add service, promotional, or imperative guidance messages.
- Do not commit private file paths, scan caches, personal inventories, credentials, screenshots, or build output. Check `.gitignore` and the staged diff before uploading changes.
