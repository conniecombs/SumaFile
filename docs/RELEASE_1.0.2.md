# SumaFile 1.0.2 Release Notes

Published November 5, 2026. For the next version, see the [1.0.3 release notes](RELEASE_1.0.3.md).

Prepared November 5, 2026 for **v1.0.2**. This release brings configurable parallel
transfers, safer copy/move recovery, clearer transfer outcomes, and improvements
to everyday pane, preview, conflict and rename workflows.

These notes compare the published **v1.0.1** tag
(`26931733711464d6272c71702741d829c8998923`, published October 20, 2026)
with the integrated application at
`3ac4bdf9651ef4277a1ff3529cfa4d3f912530bc`, plus the v1.0.2 version and
documentation preparation. The application interval contains **19 commits**.
[Review the complete application diff](https://github.com/conniecombs/SumaFile/compare/26931733711464d6272c71702741d829c8998923...3ac4bdf9651ef4277a1ff3529cfa4d3f912530bc).
Publication status and date are recorded on the GitHub release.

## Downloads and compatibility

| Artifact | Use |
| --- | --- |
| `SumaFile_1.0.2_x64-winui-setup.exe` | Recommended per-user NSIS installer |
| `SumaFile_1.0.2_x64-winui.msi` | MSI deployment package |
| `SumaFile_1.0.2_x64-winui-portable.zip` | Portable payload containing `SumaFile.exe` and `simplefile-service.exe` |
| `latest-winui.json` and setup `.sig` | Updater version, size, SHA-256 and Ed25519 signature |

Supported platform: **Windows 10 2004+ / Windows 11, x64**. The application remains
a native WinUI 3 host with a Rust service. Ship the app and service together.
The MIT license, existing settings identity, and IPC protocol version remain
in place; MIT licensing and the FTP/FTPS/SFTP manager were already present in
the published v1.0.1 baseline.

## Transfer controls and performance

- Added **Automatic, Conservative, Maximum throughput and Custom** transfer
  modes. Automatic modes compare sustained throughput while work is queued;
  Custom preserves the requested file limit within memory and provider limits.
- Added transfer defaults and per-operation overrides through the progress
  window, with persisted settings and a **Use defaults** action.
- Exposed parallel-file limits, directory checkers, backlog, file ordering,
  buffer size/budget, Windows multistream cutoff/stream count, integrity mode,
  in-place mode, preallocation, flush-on-close, bandwidth and retry controls.
- Added active-file rows, current concurrency/tuning explanations, visible
  storage-retry waits, and removal of completed rows from the transfer window.
- Local folders, removable media, mapped shares, mounted cloud drives and
  cross-volume moves share the scheduler. Same-volume moves retain the fast
  rename path. Compatibility copy/move and native remote streaming commands
  use the common policy and outcome handling.
- Directory scanning feeds a bounded queue instead of requiring all file tasks
  up front. Completed scheduler entries are released; recursive move verification
  records are spooled to a locked temporary file instead of retained in RAM.
- Windows large-file copies can use positional byte-range streams. Moves and
  checksum copies use the hashing pipeline. Reusable buffers are bounded and
  reused only at matching capacity.
- Buffer accounting includes actual stripe/pipeline allocations and verification
  scratch. Per-job budgets are 16–512 MiB; the process shares 32 file-worker
  permits and a 512 MiB active-plus-cached buffer ceiling. The shared buffer cache
  is capped at 64 MiB. These limits do not bound total process memory.
- Bandwidth zero means unlimited. Successful slow I/O, metadata operations and
  flushes do not impose a hidden rate cap. Native remote streams charge actual
  bytes, including short reads, and checksum verification traffic is paced.
- Bounded route-specific learning revalidates saved concurrency, expires records
  after 30 days, and retains at most 64 routes. Manual/bandwidth-limited runs
  do not teach a misleading throughput optimum. **Forget learned settings**
  removes saved observations.
- Bandwidth and scheduler limits refresh during a running transfer. Each newly
  dispatched file captures current integrity, storage and buffer settings;
  active files finish with their existing snapshot.

New-policy defaults include a 16-file ceiling, Custom concurrency of four,
eight checkers, a 10,000-task backlog, 4 MiB buffers, a 192 MiB buffer budget,
four streams above 64 MiB, size/modification-time checking, preallocation and
flush-on-close enabled, and no bandwidth limit. Existing saved policies are
normalized and missing fields receive defaults.
[Transfer settings and provider limits](https://github.com/conniecombs/SumaFile/blob/v1.0.2/docs/TRANSFER_TUNING.md)
describe effective limits and update timing.

## Transfer safety, integrity and recovery

- Added configurable low-level attempts and file retry passes with cancellable
  exponential waits. Defaults are ten low-level attempts and three file passes,
  with a one-second base delay; not every error is retryable. Only the failing
  file/phase waits while healthy workers continue. Authentication, certificate,
  host-key, permission, conflict, missing-file and invalid-data errors fail
  promptly. Original failures remain visible alongside later failures.
- Native remote retries reconnect and restart incomplete staged files.
  Completed files are retained rather than replayed. In-session replacement
  rollback protects existing destinations when promotion fails.
- Local staging now has locked ownership records and verified filesystem
  identities. Recovery skips live operations and unknown artifacts, handles
  torn trailing journal updates, restores recorded replacement backups when
  appropriate, and removes only verified abandoned staging.
- Recovery runs when a destination directory is visited. Interrupted local
  work restarts safely. During a live retry, a partial prefix is checked before
  appending. Unknown legacy partial names are no longer accepted as proof of
  ownership or deleted automatically.
- New/replacement directory trees remain private until copying completes and
  workers join. One outer directory lease covers unpublished descendants,
  avoiding repeated temporary-name suffixes and directory journal locks.
  Merges into existing directories remain per-file transactions.
- Windows moves delete through the verified open source handle, including
  read-only handling, so a different object replacing the original pathname
  is protected. Missing identities fail closed; changed/new sources remain.
- Incremental SHA-256 makes integrity checks independent of read boundaries.
  Download verification checks persisted local bytes and re-reads remote content;
  upload checksum mode verifies staged remote content before publication.
- Downloads require successful final source metadata validation. A missing,
  changed or non-file source is rejected before publication.
- Source-version manifests preserve valid modification timestamps before 1970.
  Flush-on-close is applied consistently to ordinary local copies and downloads.
- Internal transfer artifacts are hidden/excluded from normal listings, and
  successfully promoted files have the temporary hidden attribute cleared.
- A stopped multistream sibling no longer masks the originating I/O error with
  synthetic cancellation.
- Backend cancellation produces a structured cancelled outcome even when the
  C# caller's token was not cancelled. Committed, skipped and pending selections
  are retained for correct UI/history/undo behavior.
- Structured conflicts reach the conflict dialog. Retries exclude selections
  already committed or skipped. IPC disconnect/shutdown cancels connection-owned
  work and stops the outbound writer so progress backpressure cannot strand it.

## FTP, FTPS and SFTP

- Added provider-level stat operations: SFTP uses lstat-style metadata and FTP
  uses MLST, with conservative LIST fallback for unsupported or ambiguous replies.
  This reduces repeated parent-directory listings where the server supports it.
- Added effective checksum verification and bounded retry-pass behavior, strict
  download revalidation, clearer failures and staged finalization recovery.
- Accounted for actual provider stream-buffer sizes. The remote options dialog
  disables unsupported local-only controls and explains provider limitations.
- Native remote providers currently allow **one file stream per session**.
  A larger local file limit does not create additional remote connections.

## Panes, previews and file-operation dialogs

- Details view remains selected in narrow panes, using compact leading columns
  and horizontal access to other columns.
- Persistent pane-local filter counts, **Clear filter**, search-result banners
  and **Return to folder** make active filters/searches visible across pane changes.
- Preview can be pinned open or automatically collapse when empty or cramped.
  A collapsed rail reopens it, and automatic collapsing is configurable in
  Settings → Behavior. Preview layout preferences persist in settings/profiles.
- Conflict dialogs compare incoming and existing paths, sizes and modification
  times, and preview the proposed **Keep both** filename.
- Advanced Rename shows complete **Before/After** names, highlights changes,
  labels targets **Ready**, **Unchanged** or **Conflict**, and updates the rename
  scope while preserving extensions as configured.
- Simplified the default toolbar and made newly defaulted toolbars use icons
  with labels. Cross-pane copy/move labels indicate the destination direction;
  toolbar width calculations accommodate those labels.
- Refined pane styling, active/drop highlights and path-bar editing. Clicking
  unused breadcrumb-bar space enters path editing without intercepting buttons.
- Git status colors distinguish added/staged, modified/renamed, untracked, and
  deleted/conflicted entries. Secondary row text prioritizes Git/symlink context.
- Removed redundant preview size and metadata summary rows; normalized equivalent
  dimension values to prevent duplicate presentation.

## Dependency, CI and release maintenance

- Repaired dependency-audit failures and centralized the audit argument policy.
  Release auditing and automated security repair use the same advisory exceptions
  and denied-warning rules; this release does not claim that every existing
  advisory exception was removed.
- Added daily bounded security maintenance and audit-failure handling that
  re-audits trusted main and proposes eligible Cargo patch updates. Proposals
  receive normal tests and installer gates; unknown/compiler failures require review.
- Improved GitHub Actions permission diagnostics and the permissions-only setup
  path for repositories that disallow Actions-created PRs. Existing protections
  are preserved; interrupted proposal publication can recover.
- Added transport failure injection, resource-budget, recovery, source-identity,
  checksum, retry, cancellation, conflict and historical-timestamp regressions.
- Expanded WinUI runtime checks for pane filters, search return, previews,
  rename/conflict dialogs, transfer options, active rows, live concurrency
  overrides, storage-pressure recovery and byte-for-byte bulk copying.
- Kept 10,000- and 100,000-entry rendering/navigation coverage while using the
  smallest fixture for transfer regressions. The default ten-minute smoke no
  longer also performs an implicit 100,000-file durable-copy stress test.
  An explicit stress run remains available with
  `npm run smoke:winui-ui -- -Counts 100000 -TimeoutSeconds 3600`.
- Added atomic scenario checkpoints, timeout logging and retained
  `progress.json` evidence when a final UI report cannot be written.
- Formatting failures upload a rustfmt repair patch while independent compile/test
  diagnostics still run. Formatting remains a required passing gate.
- v1.0.2 preparation aligns Cargo/MSBuild versions, lockfiles, the Windows manifest,
  NSIS fallback, About/Settings identity and documentation. Version tooling now
  covers the manifest and NSIS fallback, and release publication uses this
  version-specific notes file after checking its heading.

### Dependency changes since the v1.0.1 tag

| Locked package | v1.0.1 | v1.0.2 |
| --- | --- | --- |
| crypto-bigint | 0.7.5 | 0.7.10 |
| ctutils | 0.4.2 | 0.4.8 |
| lofty | 0.25.4 | 0.25.5 |
| log | 0.4.34 | 0.4.35 |
| lzma-rust2 | 0.22.0 | 0.23.0 |
| notify | 8.2.0 | 8.2.1 |
| ogg_pager | 0.7.2 | 0.8.0 |
| suppaftp | 12.2.0 | 12.2.1 |
| tokio | 1.53.2 | 1.54.1 |
| typed-path | 0.12.3 | 0.13.0 |
| ureq | 3.4.2 | 3.5.0 |
| ureq-proto | 0.6.4 | 0.6.5 |
| zip | 9.0.5 | 9.2.0 |

The service now directly uses the already-locked `sha2` version. The resolved
graph adds `objc2-core-foundation`/`objc2-core-services` 0.3.2 through notify,
removes `fsevent-sys` 4.1.0, and consolidates Windows dependencies by removing
`windows-sys` 0.60.2, `windows-targets` 0.53.5 and its 0.53.1 architecture
packages. These target-specific lockfile changes do not add macOS/Linux product support.
The application's three workspace crates advance from 1.0.1 to 1.0.2.

## Upgrade from 1.0.1

Use the v1.0.2 NSIS setup executable for a normal per-user upgrade, or the MSI
for managed deployment. Portable users should replace the complete extracted
payload, including both application and service, after closing the running app.

Settings → Updates can install a newer published release only when the installed
build trusts the signing key and receives complete signed metadata. Otherwise it
opens GitHub Releases. Draft releases are not offered by the latest-release endpoint.
Version preparation alone does not publish update assets.

Existing settings, tags, smart folders, workspace/profile data and compatibility
identifiers remain in place. A toolbar matching the former factory default is
migrated to the simplified default; explicitly customized layouts continue through
the normal normalization path. Transfer defaults are persisted locally.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use.
Compatibility settings-store identifiers remain stable.

## Validation and release checklist

The application baseline passed
[CI run 130](https://github.com/conniecombs/SumaFile/actions/runs/42069291073),
including Rust quality gates, 509 .NET tests, Windows backend/WinUI builds, both
runtime smoke sizes, MSI/NSIS install checks and upgrades from the latest release
and first SumaFile baseline. This is baseline evidence; the v1.0.2 preparation PR
must pass its own CI before tagging.

For release preparation, run:

```powershell
npm ci --ignore-scripts
npm run check
npm run test:maintenance
npm run check:winui
npm run check:rust
npm run check:security
npm run smoke:winui-ui
npm run release:build
npm run smoke:winui-msi
npm run smoke:winui-installer
npm run smoke:winui-upgrade
npm run smoke:winui-upgrade-from-ref -- -PreviousRef v1.0.1
```

Verify `node scripts/release-version.mjs --resolve` selects v1.0.2 and this notes
file. Confirm all required CI/installer gates on the release commit. Production
release builds require updater signing configuration. Require a signed test release before claiming in-app updater installation is proven.
Then follow the repository release process to tag or dispatch v1.0.2.

## Dogfood 10-Step Script

1. Upgrade a disposable v1.0.1 installation; verify About and Settings show 1.0.2.
2. Confirm saved settings, tags, smart folders and workspace profiles survive.
3. Open 10,000- and 100,000-entry folders; scroll/select the last row and navigate away while loading.
4. Use narrow dual panes in Details; verify column scrolling, filter clearing and Return to folder.
5. Collapse/reopen/pin preview and confirm the automatic-collapse preference persists.
6. Exercise Rename Before/After and each conflict choice, including Keep both.
7. Change transfer defaults and per-job options; observe active files and live tuning.
8. Cancel a recursive copy/move; confirm cancelled history, retained sources and owned-staging cleanup.
9. Exercise interrupted local replacement and FTP/SFTP checksum/retry behavior using disposable fixtures.
10. Confirm installer names, Windows version identity, signed updater metadata and published notes match v1.0.2.

## Known Limitations

- Existing-directory merges and multi-selection moves are not whole-operation
  transactions. Previously committed files can remain after another file fails.
- Local crash recovery visits destination directories; unknown/unverifiable
  legacy partials remain for inspection. Recovery never deletes source data.
- Native remote process crashes can leave server-side staging/backup artifacts.
  The local journal does not provide FTP/SFTP server-side crash recovery.
- SHA-256 checks and durable journals add CPU/I/O work. CI smoke completion is
  not a throughput benchmark for physical NAS, cloud, removable storage or a
  100,000-file durable copy; provider physical durability is not guaranteed by sync calls.
- Directory bookkeeping, remote listings, top-level selections, protocol memory,
  thread stacks and OS cache are outside the copy-buffer budget.
- Native FTP/FTPS/SFTP remains a separate manager with one stream per session;
  account-backed cloud mounting and provider-internal upload queues are outside this release.
- Synchronous filesystem calls may wait on the OS/driver despite cancellation.
  Archive operations retain their separate format-specific engine.
- First installation is manual; in-app upgrades require a newer published,
  correctly signed release. The portable/installed payload includes 7-Zip tooling
  for .7z workflows. RAR listing/extraction is supported; RAR creation and
  in-place rewriting are unsupported.
- Historical SimpleFile releases use a separate version history. Their old
  “1.0.2” entry is not this SumaFile release.

## Included application commits

The following complete tag-to-baseline list includes merge and validation commits.
Release-preparation changes are reviewed in the v1.0.2 PR.

| Commit | Change |
| --- | --- |
| [`4384dd40`](https://github.com/conniecombs/SumaFile/commit/4384dd401021d204daa812789db08de9b61115be) | Polish pane, preview, and file workflows |
| [`79449e16`](https://github.com/conniecombs/SumaFile/commit/79449e16651318c8f931ce497b82c4bdbb0a5985) | Polish WinUI panes and metadata presentation |
| [`c23eac83`](https://github.com/conniecombs/SumaFile/commit/c23eac834a7e269a1b651b761b4f809f48e38f3a) | Fix dependency audits and add bounded daily security maintenance |
| [`8b480b82`](https://github.com/conniecombs/SumaFile/commit/8b480b82191ba02592ba5c26e0ddd180d270e954) | Merge pull request #39 from conniecombs/connie/maintenance/durable-ci-security |
| [`bcbe3188`](https://github.com/conniecombs/SumaFile/commit/bcbe3188fab9a0e8908f47a5b540a3c563365283) | Bump the minor-and-patch group across 1 directory with 7 updates |
| [`5f16f42a`](https://github.com/conniecombs/SumaFile/commit/5f16f42a7800031cb7f5ca8543821573dbb9e430) | Merge pull request #40 from conniecombs/dependabot/cargo/minor-and-patch-1ee0192184 |
| [`3080a7d9`](https://github.com/conniecombs/SumaFile/commit/3080a7d95e1ac08b62aa110b25aa2e52e6338863) | Add adaptive transfer tuning and parallel copies |
| [`63400beb`](https://github.com/conniecombs/SumaFile/commit/63400beb7cec58d3f3e7c571c7def0dd3773ff61) | Harden transfers with bounded retries |
| [`db98dc42`](https://github.com/conniecombs/SumaFile/commit/db98dc429fa0c5fea35248d370b717cb508bd33f) | Harden transfer safety and recovery |
| [`5d688178`](https://github.com/conniecombs/SumaFile/commit/5d6881780315fd8123b826d94cb399126e1a4ef3) | Expand transfer tuning and parallel copy support |
| [`88ae5cbf`](https://github.com/conniecombs/SumaFile/commit/88ae5cbfadc4615910ab2fb41d7895b2a31b1336) | Fix transfer staging cleanup and promotion |
| [`07b41875`](https://github.com/conniecombs/SumaFile/commit/07b41875f24de7be657585a29c2abdf94b957211) | Harden transport ownership, verification, cancellation and resource accounting |
| [`a465719e`](https://github.com/conniecombs/SumaFile/commit/a465719e9eadfd2bb14287ff3865edae0541ff84) | Validate Windows handle types and expand transport regression coverage |
| [`c23ed41e`](https://github.com/conniecombs/SumaFile/commit/c23ed41e8aa1db7603ec5f999cdee74d061587c6) | Align cancellation and scheduling tests with owned staging and actual buffer limits |
| [`20e2ff3e`](https://github.com/conniecombs/SumaFile/commit/20e2ff3e74998f347619af1d8358ba901c12404f) | Bound nested staging journals and preserve historical source timestamps |
| [`83f71a55`](https://github.com/conniecombs/SumaFile/commit/83f71a55b74b6e2ed2cc81166808b79bf6a0b27c) | Apply rustfmt to final staging changes |
| [`d11e18d7`](https://github.com/conniecombs/SumaFile/commit/d11e18d71f08fb6188e6df417ac626569dcd28e4) | Merge pull request #44 from conniecombs/codex/transport-engine-hardening |
| [`0968a8d5`](https://github.com/conniecombs/SumaFile/commit/0968a8d55f5777d356314504bcff928411c72be8) | Separate large-folder rendering from copy stress and retain smoke checkpoints |
| [`3ac4bdf9`](https://github.com/conniecombs/SumaFile/commit/3ac4bdf9651ef4277a1ff3529cfa4d3f912530bc) | Merge pull request #45 from conniecombs/connie/maintenance/ui-smoke-workload-boundary |

