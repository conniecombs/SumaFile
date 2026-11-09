# Transfer tuning

Applies to **SumaFile 1.0.3**. See the [release notes](RELEASE_1.0.3.md) for changes since v1.0.2.

Open **Transfers > Transfer defaults** to choose a policy. A running or queued task's
**Options** button overrides those defaults for that task; **Use defaults** removes the
override. Rows a plugin reports (7-Zip archive jobs, FTP/SFTP transfers) have no per-task
Options: their settings live under Settings > Plugins. Settings persist locally. Bandwidth and scheduler limits refresh within
five seconds during streamed copies.
Each newly dispatched file snapshots current integrity, storage and buffer settings;
active files finish with their current settings. Directory checkers, queue order,
backlog and local retry-pass limits are fixed when that scheduler starts.

| Mode | Behavior |
| --- | --- |
| Automatic | Starts with four files, or twelve for small-file batches (or a revalidated learned setting), then compares sustained throughput at different concurrency levels. |
| Conservative | Starts with two files and permits at most four, subject to your lower limit. |
| Maximum throughput | Starts with eight files, or sixteen for small-file batches, and explores higher concurrency within your configured limits. |
| Custom | Uses your selected file limit, subject to memory and provider limits. An I/O retry delays its failing file. |

All modes accept a parallel-file ceiling (1–32), copy-buffer budget (16–512 MiB), and
bandwidth limit in MiB/s (zero means unlimited). The buffer budget is per job; the
service additionally caps all its file workers together at 32 and their buffers at
512 MiB including the shared cache (at most 64 MiB). Reservations account for
actual stripe/pipeline layouts and verification scratch; smaller buffers cannot
reuse a larger cached allocation. Two independent jobs may run concurrently.
Conflicting destination trees remain queued. Bandwidth caps are per job, not a machine-wide network shaper.

With no stored policy, the Rust service uses a 256 MiB budget and zero extra
streams. When the WinUI options dialog has no saved values to load, it displays
192 MiB and four streams. Saving the dialog writes the displayed values to the
policy. Existing settings and per-job overrides take precedence over the defaults.

## Supported tasks

* Local, removable, mapped-network and mounted-cloud copies use the same adaptive
  file scheduler, including recursive folders and multiple selected files.
* Moves preserve the fast rename path on the same drive. Folder moves that require
  copying use the scheduler before source deletion; file selections across drive
  roots also copy concurrently. Source deletion follows successful destination
  promotion. This does not make an entire multi-file move transactional.
* FTP, FTPS and SFTP uploads/downloads run in the bundled FTP/SFTP plugin
  (`com.sumafile.remote`), not in this scheduler. They reuse the shared staging,
  journal and retry code, but read the plugin's own settings (Settings > Plugins >
  FTP/SFTP connections, or the manager's Transfer options dialog): bandwidth limit,
  up-to-date check, direct writes, pre-allocation, flushing and retries. The
  `transfer.policy` row seeds them once per legacy database on upgrade and is not
  read by remote transfers afterwards. A remote session copies one file at a time.
* Archive extraction, checksum scans, server-side operations, and a cloud client's
  internal upload queue are not file-stream jobs controlled by this scheduler.

When a batch hits a file conflict or a permanent error, the remaining files can
continue. The failure includes the exact source and destination. Resolving a
conflict or selecting Retry retries only the affected items.

The active list shows files currently assigned to workers and removes them when
finished. Workers waiting for a shared resource permit may appear in this list.
Aggregate progress, tuning reason, errors, and cancellation remain visible.

## Learning and reliability

New-policy defaults are ten low-level attempts and three file passes with a
one-second retry base. Exponential backoff uses factors 1, 2, 4, 8, then 16;
each wait is capped at 60 seconds. A file pass can contain low-level retries,
so the limits are separate. Nonretryable failures still fail immediately.

The controller compares pairs of five-second samples, ignores noisy or substantially
under-filled windows, probes additional workers, and rolls back a slower probe. It
also tests fewer workers after an unsuccessful increase and backs off when a retry
is observed. Sharing violations use a bounded retry and are excluded from the
tuner’s slowdown signal. Repeated unsuccessful upward probes increase the
cooldown, up to 32 qualified samples. Successful slow I/O does not impose a hidden speed cap. Limits protect
resources; they do not guarantee a mathematical optimum. Short jobs may finish before enough evidence is available to tune.

Proven concurrency settings are stored by ordered endpoint identity, copy/move
direction and average-file-size bucket. Mapped shares and local volume identity
help distinguish routes. Records expire after 30 days, are capped at 64 routes, and
are rechecked on reuse. Paths and account names are hashed in the learning record;
credentials and file contents are never stored there. Bandwidth-capped and manual
runs do not teach a bandwidth-limited optimum. **Forget learned settings** clears
saved history; a running task can subsequently save fresh observations.

The configurable backlog defaults to 10,000 file tasks. Completed tree tasks are
released and their source versions are spooled to disk rather than retained in RAM.
Directory bookkeeping and the user's top-level selection still grow with the
number of directories/selections. Temporary-file promotion, conflict handling,
metadata preservation, cancellation cleanup and network retry remain in the copy
path. Workers are joined before a directory is cleaned up or promoted.

For a cloud drive, observed speed is filesystem/cache throughput. The cloud client
controls caching and remote completion. Windows can split a large file across byte ranges when both endpoints qualify as
local fixed, non-virtual volumes, the stream count is at least two, and the file
meets the cutoff and memory limits. Checksum mode hashes the bytes and uses
the sequential pipeline. A move by itself does not enable hashing. FTP/SFTP plugin
sessions use provider-sized buffers, and the plugin's settings offer no concurrency
or buffer controls. The plugin's checksum check performs an additional remote read,
and retry passes never replay files already completed. Native pCloud
API/OAuth and system-wide disk/CPU utilization sensing are outside this scheduler.

See [Transport engine review and repairs](TRANSPORT_ENGINE_REVIEW.md) for ownership,
crash recovery, validation and resource-accounting details.
