# Transfer tuning

Open **Transfers > Transfer defaults** to choose a policy. A running or queued task's
**Options** button overrides those defaults for that task; **Use defaults** removes the
override. Settings persist locally. Changes are checked every five seconds during
streamed copies. Reducing a limit lets active files finish before starting more work.

| Mode | Behavior |
| --- | --- |
| Automatic | Starts with four files (or a revalidated learned setting), then compares sustained throughput at different concurrency levels. |
| Conservative | Starts with two files and permits at most four, subject to your lower limit. |
| Maximum throughput | Starts with eight files and explores higher concurrency within your configured limits. |
| Custom | Uses your selected file limit. I/O retries can temporarily reduce it for reliability. |

All modes accept a parallel-file ceiling (1–32), copy-buffer budget (16–512 MiB), and
bandwidth limit in MiB/s (zero means unlimited). The buffer budget is per job; the
service additionally caps all its file workers together at 32 and their buffers at
512 MiB. Two independent jobs may run concurrently. Conflicting destination trees
remain queued. Bandwidth caps are per job, not a machine-wide network shaper.

## Supported tasks

* Local, removable, mapped-network and mounted-cloud copies use the same adaptive
  file scheduler, including recursive folders and multiple selected files.
* Moves preserve the fast rename path on the same drive. Folder moves that require
  copying use the scheduler before source deletion; file selections across drive
  roots also copy concurrently. Source deletion follows successful destination
  promotion. This does not make an entire multi-file move transactional.
* FTP, FTPS and SFTP uploads/downloads share policy defaults and bandwidth pacing.
  Their current provider session permits one file stream. The remote window states
  this limit explicitly; selecting a higher file limit does not create extra server
  connections. Parallel remote sessions require a separate provider enhancement.
* Archive extraction, checksum scans, server-side operations, and a cloud client's
  internal upload queue are not file-stream jobs controlled by this scheduler.

The active list shows files currently assigned to workers and removes them when
finished. Workers waiting for a shared resource permit may appear in this list.
Aggregate progress, tuning reason, errors, and cancellation remain visible.

## Learning and reliability

The controller compares pairs of five-second samples, ignores noisy or substantially
under-filled windows, probes additional workers, and rolls back a slower probe. It
also tests fewer workers after an unsuccessful increase and backs off after retries
or sustained stalls. Limits protect resources; they do not guarantee a mathematical
optimum. Short jobs may finish before enough evidence is available to tune.

Proven concurrency settings are stored by ordered endpoint identity, copy/move
direction and average-file-size bucket. Mapped shares and local volume identity
help distinguish routes. Records expire after 30 days, are capped at 64 routes, and
are rechecked on reuse. Paths and account names are hashed in the learning record;
credentials and file contents are never stored there. Bandwidth-capped and manual
runs do not teach a bandwidth-limited optimum. **Forget learned settings** clears
saved history; a running task can subsequently save fresh observations.

Enumeration queues at most 128 file tasks at a time; buffer memory is bounded.
Directory bookkeeping and the user's top-level selection still grow with the
number of directories/selections. Temporary-file promotion, conflict handling,
metadata preservation, cancellation cleanup and network retry remain in the copy
path. Workers are joined before a directory is cleaned up or promoted.

For a cloud drive, observed speed is filesystem/cache throughput. The cloud client
controls caching and remote completion. Native pCloud API/OAuth transfer support,
per-file range/chunk parallelism, and system-wide disk/CPU utilization sensing are
not added by this feature.
