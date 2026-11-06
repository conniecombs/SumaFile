# Transport engine review and repairs

The initial repairs shipped in [SumaFile 1.0.2](RELEASE_1.0.2.md). The [v1.0.3 follow-up](#v103-repairs) covers per-file retries and the transfer scheduler. See the [1.0.3 release notes](RELEASE_1.0.3.md) for upgrade guidance.

Scope: local, removable, UNC and mounted-cloud copy/move; FTP/FTPS/SFTP streaming;
Rust scheduling and staging; C# terminal outcomes, conflict retries and settings.
Reviewed against `88ae5cbfadc4615910ab2fb41d7895b2a31b1336`.

## Findings and implementation

| Finding | Repair | Regression coverage |
| --- | --- | --- |
| A stopped stripe could report cancellation before the originating I/O error | Sibling shutdown is distinct from user cancellation; only the failing stripe supplies the error | Multistream short-source failure and cancellation |
| Backend cancellation could be returned as successful partial completion | Structured cancelled outcome, including committed/pending sources; C# throws `TransferCanceledException` independently of its local token | Rust cancellation and C# backend cancellation |
| Verified source handles were closed before pathname deletion | Windows deletes the verified handle, including read-only fallback; identity checks fail closed | Renamed source with a replacement at the old path; read-only files |
| Stale-artifact wildcard scans could remove another operation's data | Exclusive ownership-record locks, file identities, append-only synced replacement intent; scan each destination directory once | Active locks, unowned files, identity replacement and interrupted promotion |
| New/replacement directory trees exposed partially copied content | Build in an exclusively created sibling directory; join workers before promotion | Cancellation, mixed directory/file replacements, nested trees |
| Hash results depended on read boundaries | Incremental SHA-256 for source versions and checksum verification | Known vector and uneven read partitions |
| Reservations assumed three buffers despite up to sixteen stripes | Charge actual stripe/ring layout plus verification scratch, enforce per-job reservations, cap active plus cached buffers globally | Budget cancellation/shrink, exact-capacity reuse, tuning limits |
| Small copies could inherit a huge pooled allocation | Reuse only exact-capacity allocations; shared cache capped at 64 MiB and 32 buffers | Pool capacity and byte bounds |
| Completed tree tasks and versions stayed in memory | Release completed scheduler entries; spool recursive source-version records to a locked temporary manifest | Recursive copy/move, completion-manifest traversal and timestamps before 1970 |
| Repeated remote parent listings scaled poorly | Provider `stat`: SFTP lstat and FTP MLST, with conservative LIST fallback for unsupported FTP servers | Stat-count regression and FTP metadata parsing |
| Download revalidation ignored errors or missing sources | Require a successful final stat; reject disappeared, changed, or non-file sources before publication | Failed stat and disappearance preserve original destination |
| Remote checksum and retry-pass settings did nothing | Verify persisted/downloaded bytes and remote content using SHA-256; bounded retries apply only to the failing file | Same-length remote corruption and retry-pass limits |
| Directory workers used a policy snapshot for the whole tree | Snapshot current policy when dispatching each file; active files finish with their current settings | Policy update/dispatch tests |
| Flush-on-close skipped new ordinary local copies | Honor the selected flush policy for every local copy and download | Copy paths and policy coverage |
| Structured conflicts bypassed the conflict dialog | Handle structured conflicts and retry remaining sources, excluding committed/skipped selections | C# structured conflict filtering |
| Client disconnection left registered operations running | Cancel connection-owned transfers/scans when serving ends | Session shutdown/disconnect tests |

## Recovery contract

Local ownership records are siblings of the staging object, ending in
`.sumafile-partial.state`. They contain source/destination paths, staging identity,
and (for replacement) the original destination's backup identity. Each state change
is appended and synced **before** the corresponding replacement rename. An
exclusive file lock is held for the operation's lifetime. A torn trailing update
leaves the previous complete record usable.

When a destination directory is next visited, recovery skips locked, oversized,
unparseable and unverifiable records. It restores a recorded backup if the final
name is absent; removes that backup if the final name contains the recorded staged
object; and removes abandoned owned staging. Conflicting identities are retained
and reported. Recovery never deletes source data. Unknown legacy partials are left
intact; their names alone do not prove ownership.

An interrupted transfer is safely restarted, not blindly resumed from byte counts.
Within an active operation, a low-level retry verifies a partial file's prefix
before appending. New directories are published after their complete copy. One
outer directory lease protects unpublished child directories, avoiding a locked
journal and another staging-name suffix at every nesting level. Merges into
existing directories remain per-file transactions: previously committed files
can remain after another file fails. Moves remove only verified copied sources;
new or changed sources remain and the operation reports the failure.

Remote uploads use unique temporary/backup names and bounded reconnect/rollback
within the running session. The local journal does not claim crash recovery for
server-side FTP/SFTP operations: a service/process crash can leave those remote
artifacts for inspection. No source is deleted by a native remote copy. Filesystem
sync requests cannot guarantee a NAS or cloud provider's physical durability.

## Resource and performance contract

Active copy-buffer reservations are bounded per job (16–512 MiB). All jobs share
32 file-worker permits and a 512 MiB limit covering active reservations plus the
buffer cache. The cache is at most 64 MiB. Local reservations include up to sixteen
stripe buffers or three pipeline buffers and 128 KiB for verification scratch.
Remote reservations account for the provider's actual stream buffer size. These
are application copy-buffer limits, not a bound on total process RSS, OS cache,
thread stacks, protocol libraries, directory metadata or IPC payloads.

The scheduler bounds pending file records and removes successful entries as they
complete. Recursive completion versions are disk-backed. Directory bookkeeping,
remote folder listings/outcomes and top-level selections still scale with their
respective item counts. Default backlog is 10,000; checker-to-coordinator queues
are capped at 8,192. Configured limits are bounded; discovery order can start after
64 records instead of waiting for the whole backlog.

SHA-256 and explicit checksum mode cost CPU and verification I/O. Native remote
checksum mode reads remote content again and charges that traffic to bandwidth
pacing without inflating copied-byte progress. FTP servers without MLST still need
LIST fallback; SFTP avoids full parent listings for per-file stat and temporary-name
selection. FTP MLST avoids listings for present entries; ambiguous 550 replies
still require a successful parent listing so permission failures are not mistaken
for absence. Recovery adds one local directory scan rather than one scan for every
file.

## Validation

The PR's CI is the authority for Windows compilation and runtime results. Relevant
gates are rustfmt, Clippy with warnings denied, all Rust tests, repository/schema
checks, dependency audit, WinUI/.NET tests, XAML build and the 10,000-item UI smoke
suite. Formatting failures now provide an applyable patch without preventing the
independent compile/test diagnostics from running; a formatting failure still
fails the gate.

Tests use temporary local files, Windows handles, failure-injecting remote
providers and a loopback FTP fixture. They do not establish throughput on a
physical NAS, removable device or live cloud account. No production files or
remote credentials are needed for the regression suite.

## v1.0.3 repairs

A second review on `2086121` (`docs/superpowers/specs/2026-11-05-transport-engine-audit.md`)
led to the repairs designed in `docs/superpowers/specs/2026-11-05-transport-engine-repairs-design.md`.
In short: one coordinator per selection that continues past per-file failures and reports each
with its destination and kind; a targeted retry (`items`) so the conflict dialog acts on the file
it shows; structured failure kinds; roughly half the per-file metadata round trips (no DACL copy,
metadata set on the open handle, no post-rename attribute fix-up); multistream opt-in with its own
handle per stripe and a sparse staging file; hashing only in Checksum mode; same-volume moves that
rename, keep-both included; a tuner that ignores sharing violations and reaches the configured
concurrency with the default budget.
