# Transfer tuning

Applies to **SumaFile 1.0.3**. See the [release notes](RELEASE_1.0.3.md) for changes since v1.0.2.

Open **Transfers > Transfer defaults** to choose a policy. A running or queued task's
**Options** button overrides those defaults for that task; **Use defaults** removes the
override. Rows a plugin reports (7-Zip archive jobs, FTP/SFTP transfers) have no per-task
Options: their settings live under Settings > Plugins. Settings persist locally. Bandwidth and scheduler limits refresh within
five seconds during streamed copies.
Each newly dispatched file snapshots current integrity, storage and buffer settings;
active files finish with their current settings. Directory checkers, queue order,
whether small and large files get separate lanes, backlog and local retry-pass limits
are fixed when that scheduler starts.

| Mode | Behavior |
| --- | --- |
| Automatic | Starts from the kind of drives involved (see [How the scheduler decides](#how-the-scheduler-decides)), or from a revalidated learned setting, then compares sustained throughput at different concurrency levels. On drives it cannot identify it starts with four files, or twelve for small-file batches. |
| Conservative | Starts with two files (one on a hard disk, removable drive or optical disc) and permits at most four, subject to your lower limit. |
| Maximum throughput | Starts higher (eight files, or sixteen for small-file batches, on drives it cannot identify) and explores higher concurrency within your configured limits. |
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

## How the scheduler decides

These four rules apply to the Automatic, Conservative and Maximum throughput
modes on the **unreleased Beta branch**. Custom is unchanged: it uses your file
limit as one limit and measures nothing.

**It starts from the hardware.** Before anything is measured, the first number
of simultaneous files comes from the slower of the source and destination
drives, the mode, and whether the files are small (a batch averaging 256 KiB
or less) or large:

| Slower end of the route | Conservative | Automatic | Maximum throughput |
| --- | --- | --- | --- |
| Hard disk (reports a seek penalty) | 1 | 1 large, 2 small | 2 |
| Optical disc | 1 | 1 | 1 |
| Removable drive (USB stick, card) | 1 | 2 | 4 |
| Network share or mapped drive | 2 | 3 large, 8 small | 6 large, 12 small |
| Solid-state drive on both ends | 2 | 6 large, 16 small | 8 large, 24 small |
| Not identified (cloud mount, RAM disk, a disk whose driver does not say) | 2 | 4 large, 12 small | 8 large, 16 small |

With two lanes (below), the "small" numbers for network and unidentified
routes apply only when lanes are off; the small-file lane has its own starting
point on those routes.

A hard disk on either end sets the pace, because parallel files make its head
seek. Windows is asked once per transfer whether each fixed disk has a seek
penalty; a disk that does not answer counts as not identified. A remembered
setting for the route still wins over this table, and your file ceiling and
buffer budget still cap it.

**It reads speed in short windows.** A measuring window closes after at least
one second once enough data has moved (eight buffers, and at least 16 MiB for
ordinary files), or after two seconds. Three windows that agree make a
reading, so a steady transfer reaches a decision in about three to five
seconds and a test of more files takes one window longer. The noisier the
windows, the more of them a reading needs (up to twelve); a transfer too
erratic to average is left alone, and a test that cannot be read within 24
windows is undone. A noisy reading also has to show a larger gain before more
files are kept. On a link too slow to fill a window, the window stretches to
five seconds.

**Small and large files get a lane each.** Files under 1 MiB and files of
1 MiB or more are queued separately and run side by side, each lane with its
own number of simultaneous files that adapts on its own. Together they never
exceed your file ceiling; the buffer budget and the bandwidth limit are shared
as before. When both lanes have work each gets at least one file, the file
that has waited longest is sent first, and a lane that runs out of work gives
its share to the other. Only one lane tests a new setting at a time. With both
lanes active the Transfers window shows, for example, "4 large + 12 small".
Lanes are off, and all files share one queue, when the mode is Custom or when
you choose a queue order (by size or by name).

**The small-file lane counts files, not bytes.** For files under 1 MiB the
time goes into opening, creating and renaming each file (and, on a cloud
drive, fetching it), not into its bytes, and bytes per second swings with the
file sizes. So this lane is judged on completed files per second. Its window
closes after at least one second once 32 files have finished, or after two
seconds (stretching to five when files finish slowly). The large-file lane
and single-queue transfers still read bytes per second.

Where a network share or an unidentified drive (a cloud mount, for example) is
at either end and no hard disk, removable drive or optical disc is involved,
that per-file wait happens off your machine, so the small-file lane leans
towards more files at once:

* It starts with 16 files in Automatic and 24 in Maximum throughput (2 in
  Conservative) instead of 8 or 12. Sixteen was the whole default ceiling
  before, and each file in this lane holds one small buffer.
* It tries more files first. It tests fewer only after a step up measurably
  lowered files per second (by 5% or more), not merely because the step did
  not help, and write latency does not overrule files per second here.
* While "Most files at once in automatic modes" is still the default of 16,
  this lane alone may grow to 32, within the buffer budget; the two lanes
  together then stay within 32 and large files within 16. Any other value
  is treated as your choice and is the ceiling for both lanes together;
  Conservative keeps its limit of four and Custom its file limit. SumaFile
  cannot tell an untouched 16 from a deliberately chosen 16: to hold small
  files to fewer than 32 on such a route, set the ceiling to another number
  (15, for instance).

On hard-disk, removable and optical routes the small-file lane keeps the
starting points in the table above and tests fewer files after any
unsuccessful step up.

An I/O retry still halves the lane at once, in every window that sees one, so
a provider that starts throttling brings it down within seconds. The setting
is then held for four readings; each later bout of retries in the same
transfer doubles that hold (8, 16, up to 64 readings), so a throttling
provider is not pushed again straight away. This applies to both lanes.

**It watches write latency as an early warning.** Each write of 64 KiB or more
is timed. When the typical (median) write in a lane takes at least twice as
long as that lane's own baseline for three windows in a row, a running test of
more files is ended and the previous setting kept ("Write latency rose;
keeping the previous setting"). At a settled setting the scheduler holds
("Write latency rose; holding") and next checks whether fewer files are as
fast. Latency alone never reduces the number of files, and one slow write
(an antivirus scan, a cloud file being downloaded) does not move the median.

**Small files skip the size reservation.** With pre-allocation on, the
destination's size is reserved before writing only when the file is larger
than one copy buffer (512 KiB on network and cloud routes, 4 MiB otherwise,
or your chosen buffer size). A smaller file is written by a single write, so
reserving its size first was one extra request per file to a network share
with nothing gained. Larger files are reserved as before. Pre-allocation was
never a free-space check (its result is ignored), so a full destination is
reported by the write itself, as before.

## Learning and reliability

New-policy defaults are ten low-level attempts and three file passes with a
one-second retry base. Exponential backoff uses factors 1, 2, 4, 8, then 16;
each wait is capped at 60 seconds. A file pass can contain low-level retries,
so the limits are separate. Nonretryable failures still fail immediately.

The controller compares readings built from short windows (see
[How the scheduler decides](#how-the-scheduler-decides)), ignores noisy or substantially
under-filled windows, probes additional workers, and rolls back a slower probe. It
also tests fewer workers after an unsuccessful increase and backs off when a retry
is observed. Sharing violations use a bounded retry and are excluded from the
tuner’s slowdown signal. After a retry or an unsuccessful probe it holds the setting
for four readings; repeated unsuccessful upward probes, and repeated bouts of retries,
double that, up to 64 readings. Successful slow I/O does not impose a hidden speed cap. Limits protect
resources; they do not guarantee a mathematical optimum. Short jobs may finish before enough evidence is available to tune.

Proven concurrency settings are stored by ordered endpoint identity, copy/move
direction and average-file-size bucket; with two lanes, each lane stores its own, and
the small-file lane's files-per-second settings are stored apart from settings learned
from bytes per second (older small-file records for a route are not reused and expire). Mapped shares and local volume identity
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
