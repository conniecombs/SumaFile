# Bugs And Follow-Ups

This file tracks current Windows-focused follow-up areas.

## Active Areas To Watch

| Area | Notes |
| --- | --- |
| Drive listing | Preserve volume labels and mapped network share names. |
| Folder navigation | Directory clicks should stay inside SimpleFile. |
| Transfers | Conflict handling must preserve skip, replace, keep-both, and cancellation behavior. |
| Archives | Virtual archive paths must not bypass destination validation. |
| Preview | Large files should respect preview limits and avoid blocking the UI. |
| Installer smoke | NSIS and MSI artifacts should be verified before release. |
| Updater | Release metadata should prefer the Windows installer path. |

## Useful Commands

```powershell
npm run check
npm run check:winui
npm run check:rust
npm run smoke:winui
npm run smoke:winui-msi
npm run smoke:winui-installer
```

## Regression Notes

- When drive labels regress, start in `crates/simplefile-core/src/drives.rs`.
- When folder clicks open Windows Explorer, check `src-winui/SimpleFile.Core/ExplorerWorkspace.cs` and `crates/simplefile-core/src/preview.rs`.
- When settings regress, check `src-winui/SimpleFile.Core` workspace/settings persistence and `crates/simplefile-core/src/settings_store.rs`.
