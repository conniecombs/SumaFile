# File Operations Parity: Tauri/Svelte → WinUI/IPC

**Status:** Historical parity notes. Svelte/Tauri is no longer the shipping UI.

Tracking file operation feature parity between the Tauri/Svelte frontend
and the WinUI 3 native app.

## Status

| Feature | Tauri/Svelte | WinUI/IPC | Notes |
|---------|:---:|:---:|-------|
| Create folder | ✅ | ✅ | `create_directory` via IPC |
| Create file | ✅ | ✅ | `create_file` via IPC |
| Rename | ✅ | ✅ | Case-only rename via temp path |
| Batch rename | ✅ | ✅ | Two-phase temp strategy |
| Copy (single) | ✅ | ✅ | With CONFLICT detection |
| Move (single) | ✅ | ✅ | Cross-device fallback to copy+delete |
| Copy with conflict resolution | ✅ | ✅ | skip/replace/keep-both |
| Move with conflict resolution | ✅ | ✅ | skip/replace/keep-both |
| Copy with progress | ✅ | ✅ | `operation-progress` events |
| Move with progress | ✅ | ✅ | `operation-progress` events |
| Cancel operation | ✅ | ✅ | `cancel_operation` via IPC |
| Delete (permanent) | ✅ | ✅ | Symlink-aware |
| Trash | ✅ | ✅ | TRASH_UNAVAILABLE prefix |
| Shell open | ✅ | ✅ | `cmd /c start` |
| Reveal in folder | ✅ | ✅ | `explorer /select,` |
| Conflict dialog | ✅ | ✅ | Skip/Replace/Keep Both/Apply All |
| Progress panel | ✅ | ✅ | With cancel button |
| Keyboard shortcuts | ✅ | ✅ | F2, Del, Shift+Del, Ctrl+C/X/V/N |
| Archive-aware ops | ✅ | ❌ | Deferred to Archives phase |
| Undo/redo stack | ❌ | ❌ | No centralized undo in either |
| Clipboard cut visual | ✅ | ✅ | Cut items dim in the file list |
| Drag and drop | ✅ | ✅ | Internal move/copy + external StorageItems copy |

## Architecture

```
WinUI App → FileOperationService → IPC Client → Named Pipe → Rust Service → simplefile-core::file_ops
```

## Error Handling

The Rust service returns errors with standardized prefixes:
- `CONFLICT:` — destination exists, UI should show conflict dialog
- `TRASH_UNAVAILABLE:` — trash service unavailable (e.g. network drive)
- `HOST_OWNED:` — operation requires host UI (e.g. file picker)

The C# `FileOperationService` provides `IsConflict()` and `IsTrashUnavailable()`
static methods for prefix detection.

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+Shift+N | New folder |
| Ctrl+N | New file |
| F2 | Rename |
| Delete | Move to trash |
| Shift+Delete | Permanent delete |
| Ctrl+C | Copy |
| Ctrl+X | Cut |
| Ctrl+V | Paste |
