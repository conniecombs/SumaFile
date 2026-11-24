# Transfers

This is the reference for how SumaFile copies and moves files between folders
that Windows reaches by path (internal and removable drives, mapped drives and
UNC shares, mounted cloud drives) and for the settings that tune it. This work
runs in SumaFile's file service. FTP, FTPS and SFTP transfers run in the
FTP/SFTP plugin with settings of their own; see
[FTP, FTPS and SFTP](#ftp-ftps-and-sftp). Archive creation and extraction use
separate engines and are not covered here. [Architecture](ARCHITECTURE.md)
says where the code is; the [changelog](CHANGELOG.md) says what changed in
each release.

Words used below: a *task* is one row of the Transfers window. A *pass* is one
run of the engine over a task's items; answering the retry prompt or a
conflict starts a further pass. Within a pass a failing file gets several
*tries* per *round*, and may get several rounds.

## Where the settings are

The defaults for all transfers are in the **Transfer defaults** dialog, which
opens from two places:

* the **Transfer defaults** button at the bottom of the Transfers window;
* **Settings > Behavior > Transfers > Open transfer options...**.

The dialog shows Mode, the bandwidth limit and the checksum checkbox, and
"Files at once" when the mode is Custom. Every other setting is under
**Advanced**, which is collapsed when the dialog opens. **Forget learned
settings** is at the bottom of this dialog only.

One task can have settings of its own. Each row of the Transfers window has a
**Details** section, collapsed by default, which holds the tuning line (mode,
current file limit and the scheduler's latest reason), the list of files being
copied and an **Options** button. Options opens the same dialog as **Task
transfer options**; its **Use defaults** button removes the task's own
settings. Options is available while a task is queued or running. Rows
reported by a plugin or an archive job (FTP/SFTP transfers, 7-Zip jobs) have
no Options; their settings are under Settings > Plugins.

The Transfers window opens from the Status Center button in the status bar
(**Transfers**) or the **Show transfers** command, and comes to the front on
its own when a transfer is still running after three seconds.

## Settings

The first four rows are the controls shown at once; the rest are under
Advanced. Defaults are the file service's.

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| Mode | Automatic | Automatic, Conservative, Maximum throughput, Custom | How the number of files copied at once is chosen. See [Modes](#modes). |
| Files at once | 4 | 1–32 | Shown only in Custom mode: the fixed number of files copied at once, within the caps listed below the table. |
| Bandwidth limit (MB/s, 0 = unlimited) | 0 | 0–1,048,576 | Byte-rate limit for one task. The unit is mebibytes: 1 means 1,048,576 bytes per second. 0 means no limit, in every mode. |
| Verify copies with a checksum (slower) | Off | | The same setting as **Checksum** under "Decide a file is already up to date by"; changing one changes the other. See [Check modes](#check-modes). |
| Most files at once in automatic modes | 16 | 1–32 | Ceiling for Automatic and Maximum throughput. Conservative never exceeds 4. It also caps Custom. |
| Folder scanners | 8 | 1–64; the engine uses at most 32 | Threads that read source folders and create destination folders while files are copied. |
| Maximum queued items | 10,000 | 128–100,000 | How many discovered files may be queued, in progress or waiting for another round before folder scanning pauses. |
| Transfer order | As found | As found, Smallest first, Largest first, Alphabetical | Order in which queued files start. Any choice other than As found puts all files in one queue instead of two lanes. Sorting covers the files found so far, not the whole tree in advance. |
| Stream buffer size (KiB) | 4096 | 64–65,536 | Size of one copy buffer. While it is 4096, a file with a network or cloud drive at either end uses 512 KiB. A buffer is never more than a third of the copy buffer budget. |
| Copy buffer budget (MiB) | 256; the dialog shows 192 when nothing has been saved | 16–512 | Memory one task may reserve for copy buffers. |
| Multi-thread cutoff (MiB) | 64 | 1–16,384 | Smallest file that may be copied in several streams. |
| Streams per large file | 0; the dialog shows 4 when nothing has been saved | 0–16 | Byte ranges of one large file copied in parallel. 0 or 1 turns this off. See [Large files in several streams](#large-files-in-several-streams). |
| Decide a file is already up to date by | Size and modification time | Size and modification time, Size only, Checksum, Always copy | See [Check modes](#check-modes). |
| Write directly to the destination instead of a temporary file | Off | | See [Write directly to the destination](#write-directly-to-the-destination). |
| Pre-allocate disk space | On | | Reserves a file's size at the destination before writing it. See [Pre-allocation](#pre-allocation). |
| Flush to disk before finishing each file | On | | Asks Windows to write the file's data to the disk before the file gets its final name. |
| Retry a failing file up to | 10 | 1–50 | Tries one file gets in one round, the first included. |
| Retry rounds for the whole task | 3 | 1–20 | Rounds one failing file gets, the first included. The count is kept per file, not per task. |
| Wait before retrying (seconds) | 1 | 0.01–60 | Wait after the first failed try. Later waits are longer; see [Failures and retries](#failures-and-retries). |

**Custom is capped.** "Files at once" cannot exceed "Most files at once in
automatic modes": Files at once 32 with that setting left at 16 runs 16 files.
It cannot exceed what the copy buffer budget holds, which is the budget
divided by three buffers plus 128 KiB: 21 files with a 256 MiB budget and
4 MiB buffers, 15 with 192 MiB. The service-wide [limits](#limits) apply as
well.

**What Apply writes.** In Transfer defaults, Apply with nothing changed writes
nothing. After any change it writes the whole policy, every field, including
values the dialog displayed that you did not touch. This matters the first
time. With nothing saved the service uses a 256 MiB budget and 0 streams, but
the dialog displays 192 MiB and 4 streams, and the first change to any setting
saves those two values with it. From then on files of 64 MiB or more between
local fixed disks are copied in 4 streams, and the large-file limit between
local drives is 15 instead of 16. In Task transfer options, Apply always
stores the displayed values as that task's settings, changed or not.

**When a change takes effect.** A running task reads its settings again every
five seconds, and at once when a file is retried after an I/O error. Mode, the
two file limits, the bandwidth limit and the buffer budget then apply to the
running task. The check mode, write-directly, pre-allocation, flushing, buffer
size, the stream settings, and the tries and wait are taken by each file when
it starts; a file already being copied finishes with the settings it started
with. Folder scanners, Maximum queued items, Transfer order and the number of
rounds are fixed when a pass starts copying. A task with settings of its own
ignores the defaults; its settings are honored for 24 hours after they were
last saved, and after that the task follows the defaults.

## Modes

| Mode | Behavior |
| --- | --- |
| Automatic | Starts from the kind of drives involved (see [How the scheduler decides](#how-the-scheduler-decides)), or from a remembered setting that it measures again, then compares sustained throughput at different numbers of files. On drives it cannot identify it starts with four files, or twelve for a batch of small files. |
| Conservative | Starts with two files (one when a hard disk, removable drive or optical disc is involved) and never exceeds four, or your ceiling if that is lower. |
| Maximum throughput | Starts higher (eight files, or sixteen for a batch of small files, on drives it cannot identify) and explores up to your ceiling. |
| Custom | Uses "Files at once" as one fixed limit and measures nothing. An I/O retry delays only the file that failed. |

Zero bandwidth means unlimited in every mode; no mode adds a rate limit of its
own. A slow drive, a slow cloud read or a long flush is not an error and never
caps the speed.

## How a file is copied

Unless [Write directly to the destination](#write-directly-to-the-destination)
is on, every file is copied in these steps:

1. In the destination folder the service creates a partial file named
   `.<name>.<16 hex digits>.sumafile-partial` and beside it an ownership
   record with the same name plus `.state`. The hex digits are a hash of the
   destination path and the ID of the pass, which keeps the partial files of
   different tasks apart.
2. The record is a small text file. It names the source, the destination, the
   partial file and the partial file's identity on its volume, and later the
   backup of a file being replaced. The service keeps the record open and
   locked for as long as it works on the file, and writes each change to disk
   before the step that the change describes.
3. The bytes are written to the partial file, which carries the Windows Hidden
   attribute while it is written.
4. After the last byte the service checks that the source still has the size
   and times it had when it was opened; if not, the file fails ("Source
   changed during transfer"). It flushes the partial file to disk when "Flush
   to disk before finishing each file" is on, gives it the source's created,
   accessed and modified times and its read-only attribute, and clears the
   Hidden attribute. In Checksum mode it then reads the partial file back and
   compares it with what was read from the source.
5. The partial file is renamed to the final name. This rename never
   overwrites: unless Replace was chosen, a file found under that name is a
   conflict.
6. The record is deleted. In a move, the source is deleted next.

A half-written file therefore never appears under its final name, and an
existing file is not touched until its replacement is complete. A full disk is
found by the write that hits it; free space is not checked beforehand.

Nothing but the content, the three times and the read-only attribute is
carried over: not the Hidden or System attribute, alternate data streams or
the access-control list. If the destination refuses the times or attributes,
the file is kept as it was written. A file that a move renames (see
[Moves](#moves)) is the same file and keeps everything.

### Folders

Folders are not staged. A folder that does not exist at the destination is
created under its final name when the scan reaches it, and its files are then
copied into it one by one as above. A folder that already exists is merged
into. When all files are done, each folder the task created gets its source
folder's modified and accessed times and read-only attribute; folders that
existed before are left as they were, and a cancelled task skips this step.

A symbolic link is recreated as a link, through the same partial name and
rename; what it points to is not copied.

### When the destination exists

Copying or moving a folder into a same-name destination folder merges their
contents automatically at each level. An empty destination folder fills immediately.
Files with different names coexist. For example, if both folders contain
`2025/family.jpg`, that file prompts for **Replace**, **Skip** or **Keep both**.
A file and a folder occupying the same path also require a decision.

SumaFile can ask about top-level conflicts before copying. Conflicts found
inside a folder or created during a transfer are reported after the pass.
An answer applies to that path. **Do this for all remaining conflicts**
reuses the answer for subsequent conflicts in the same transfer.

* **Replace.** The new file is written to its partial file first. Then the
  existing file is renamed to
  `.<name>.<process id>.<number>.sumafile-replace-backup`, the partial file is
  renamed into place, and the backup is deleted. The backup's name is written
  to the ownership record before the first rename. If the second rename fails,
  the backup is renamed back and the file is reported as failed. Replace never
  puts a file in place of a folder or a folder in place of a file: such an
  item is reported as a conflict, and nothing is deleted to make room.
* **Skip.** The existing file stays and the item is reported as skipped.
* **Keep both.** The new file is written as `name (1).ext`, or with the first
  free number.
* **Undo and Redo.** A merge records the files it added and the folders it
  created. Undo recycles copied files or moves files back to their source
  paths. It removes a newly created directory only while it is empty. Redo
  replays the recorded additions. A replaced file cannot be restored by Undo.
  Skipping all files in a copy into an existing folder creates no Undo entry
  unless the copy also created a directory.

### Moves

* On the same drive or share, a selected folder whose name is free at the
  destination is moved by one rename, and so is a selected file. Nothing is
  copied and no partial file is used. If the rename is refused, the item is
  copied instead. One refusal is not answered by a copy: a file that another
  program holds open (a sharing or lock violation) could not be deleted after
  a copy either, so it is reported as busy, gets its further rounds like any
  busy file, and is not copied.
* In every other case (another drive, or a folder merged into an existing
  one) each file is copied, or on the same drive renamed, on its own, and its
  source is deleted right after that file has its final name. Before deleting,
  the service checks that the source is still the file it copied: same size,
  same times, same file identity, and in Checksum mode the same content. A
  source that changed is kept, and the file is reported as "Copied but failed
  to delete source".
* When all files are done, the source folders are removed, deepest first. A
  folder that still holds a failed or skipped file stays; a folder that holds
  anything else, such as a file added during the move, stays and is reported.
  Resolving a conflict in a later pass moves the selected file. Its source
  parent can remain empty because that pass processes only the file.
* A same-drive move with Replace renames the existing file to a
  `.sumafile-replace-backup` name, renames the source into place and deletes
  the backup. It writes no ownership record.
* A move is not all-or-nothing. After a failure or a cancel, some files are at
  the destination only and the rest are at the source only.

### Large files in several streams

A file is copied as several byte ranges at once when all of these hold:

* "Streams per large file" is 2 or more;
* the file is at least "Multi-thread cutoff" in size and at least two buffers
  long;
* source and destination are both on local fixed disks that are not cloud or
  other virtual file systems;
* the check mode is not Checksum;
* its partial file is still empty (a try that continues a partly written file
  uses one stream).

Each stream has its own buffer, and the number of streams is reduced to what
the copy buffer budget holds. If a try in several streams fails, its partial
file is emptied and the next try starts at the first byte.

### Pre-allocation

With "Pre-allocate disk space" on, a file's size is reserved at the
destination before writing only when the file is larger than one copy buffer
(512 KiB with a network or cloud drive at either end, 4 MiB otherwise, or your
buffer size) and at least 64 KiB. A smaller file is written by a single write,
so reserving its size first would be one extra request per file. A file copied
in several streams is always reserved. The reservation is not a free-space
check: its result is ignored.

### Write directly to the destination

With "Write directly to the destination instead of a temporary file" on, a
file is created and written under its final name, and is not hidden. There is
no partial file and no rename. The ownership record is still written beside
the file, as `.<name>.<16 hex digits>.sumafile-partial.state`, and deleted
when the file is complete.

* A new file is visible under its final name while it is being copied.
* With Replace, the existing file is emptied and written over in place. There
  is no backup: once the copy starts, the old content is gone.
* An existing destination for which no answer was given is reported as a
  conflict before anything is written. Skip and Keep both work as they do
  without this option.
* If the copy fails or is cancelled, the service deletes the file it was
  writing. After a failed or cancelled Replace neither the old file nor the
  new one is left at the destination. The source is not touched.
* If the service stops while such a file is being written, the half-written
  file stays under its final name with its record, and the next transfer into
  that folder deletes it (see [Recovery](#recovery)).

The note under this option in the dialog says that a cancelled or failed
transfer leaves partial files behind. The engine deletes them, as described
here; what stays behind is the folders it created and the files it finished.
Folders are created under their final names with or without this option.

## What a transfer can leave in a destination folder

| Name | What it is | Normal lifetime |
| --- | --- | --- |
| `.<name>.<16 hex digits>.sumafile-partial` | A file being copied | From the start of the file's copy until it is renamed; deleted when the file fails or the task is cancelled |
| `.<name>.<16 hex digits>.sumafile-partial.state` | The ownership record of that file | The same; with write-directly on, it sits beside the file itself |
| `.<name>.<process id>.<number>.sumafile-replace-backup` | The existing file, moved aside by Replace | Between the two renames of a Replace |

All three names begin with a dot, so SumaFile lists them only when hidden
files are shown. Only the partial file has the Windows Hidden attribute, and
only until it has been written in full. To other programs the record, the
backup and a fully written partial file waiting for its rename are ordinary
visible files.

They outlive their transfer in three cases: the service stopped in the middle
of a file (a crash, a power loss, a killed process); the clean-up after a
failure or a cancel could not delete a partial file because another program
held it open (the service tries five times in about 0.4 seconds); or a file
was published but its record could not be deleted at that moment.

### Recovery

Recovery is the clean-up of what an interrupted transfer left. It runs at the
start of every copy or move in the destination folder, and then in each
existing subfolder the transfer is about to write into. A Retry runs it in the
folder of each retried item, and an FTP/SFTP download runs it in its local
destination folder. It does not run when SumaFile starts or when a folder is
only opened.

Recovery reads every `.sumafile-partial.state` record in the folder. It acts
only on the files a record names, and only when the file it finds is the very
file that was recorded (same volume and file ID), so a different file under
the same name is never touched. It never deletes or changes a source. For each
record it does the following.

1. It leaves the record and its files alone when the record is locked by a
   running transfer, is larger than 4 MiB, has no complete line, cannot be
   read, or names files outside this folder.
2. If the record names a backup and the backup is still the recorded file: it
   renames the backup back to the original name when that name is free, which
   restores the original; it deletes the backup when that name holds the
   recorded new file, because the Replace had completed. If the name holds any
   other file, it keeps everything.
3. It deletes the partial file when that is the recorded file. For a
   write-directly record this is the half-written file under its final name,
   unless the record says the file was completed.
4. It deletes the record.

When a recorded file has been replaced by a different file, recovery keeps the
record and the files and goes on to the next record; they stay until someone
removes them. The file service writes nothing to a log when this happens.

A partial file with no record beside it is never deleted automatically: its
name alone does not prove which transfer owns it. Within a running pass, a
later round of a file that finds the record of its own earlier round (left by
a clean-up that failed) recovers it in the same way and reuses the record.

### Deleting leftovers by hand

Let recovery try first: copy any file into the folder with SumaFile and wait
for the task to finish. What is still there afterwards was not claimed by
recovery. Then, with hidden files shown and with no transfer into that folder
running or queued (FTP/SFTP downloads included):

* **`.sumafile-partial` files** are incomplete copies. Deleting one loses
  nothing: a copy never changes its source, and a move deletes a source only
  after the file has its final name.
* **`.sumafile-partial.state` records.** Delete each one together with its
  partial file. A record whose partial file is gone is harmless; recovery
  deletes it the next time.
* **`.sumafile-replace-backup` files** are original files that a Replace was
  moving aside. Look before deleting. If the file's own name is missing from
  the folder, the backup is the only copy of the original: rename it back by
  removing the leading dot and the
  `.<process id>.<number>.sumafile-replace-backup` ending. If the name is
  present, the backup is the old version and can be deleted once the new file
  has been checked. A backup left by a same-drive move has no record, so
  recovery never restores it; it has to be renamed back by hand.

The [changelog](CHANGELOG.md) entry for 1.1.4 describes copies that failed in
earlier versions with "The parameter is incorrect" and left their partial
files behind. Treat those the same way: run a transfer into the folder, then
delete by hand what is still there.

## Cancel

Cancel on a queued task ends it at once; nothing has been written. Cancel on
a running task shows **Cancelling** until the service has stopped every worker
and finished its clean-up, and only then **Cancelled**.

* Files in the middle of being copied are abandoned. Their partial files and
  records are deleted; with write-directly on, the half-written files
  themselves are deleted.
* Files that already have their final names stay at the destination. So do
  the folders created so far, which can be empty or incomplete.
* A cancelled copy does not touch its sources. In a move, every file that got
  its final name before the cancel has already lost its source (or was
  renamed), so it exists at the destination only. Source folders are not
  removed.
* Files that had not started are left alone.

Cancel is seen between reads and writes and during every retry wait. A read or
write that is blocked inside a driver (a cloud mount fetching a file, a
stalled share) is not interrupted; the cancel takes effect when that call
returns.

## Failures and retries

A file that fails does not stop the task. The other files continue, and each
failure is collected with its exact source and destination.

### Which errors are retried

| Kind | Errors | What happens |
| --- | --- | --- |
| Transient | Timeouts; interrupted and would-block calls; connection reset or aborted; not connected; broken pipe; Windows errors 21 (device not ready), 54 (network busy), 64 (network name no longer available), 121 (semaphore timeout), 170 (resource in use), 995 (I/O aborted), 1117 (I/O device error), 1231 (network unreachable), 1450 and 1453 (insufficient system resources or quota); a checksum mismatch in Checksum mode | Tried again, up to "Retry a failing file up to" |
| Busy | Windows errors 32 (sharing violation) and 33 (lock violation): another program holds the file | 3 tries while copying; the full number of tries for the rename that gives the file its final name |
| Conflict | The destination exists, or exists as a different type | Not tried again; you are asked |
| Permanent | Everything else, including access denied, file not found, invalid data, a full disk, and a source that changed or shrank during the copy | Not tried again |

### Tries and rounds

There are two levels, with separate limits.

**Tries within a round.** After a transient or busy error the file waits and
is tried again. The waits are the "Wait before retrying" value times 1, 2, 4,
8 and then 16 for every later wait, each at most 60 seconds. With the defaults
(10 tries, 1 second) one round waits at most 95 seconds in total. A new try
continues the partial file from its current length; bytes already written are
not copied again. Before each new try the source is checked, and if its size
or modified time has changed the file fails for good ("Source changed before
retry"). While a file waits, the Details line shows "Retrying failed I/O" with
the failing step, the path and the error, and the other files keep running. A
transient error (not a busy file) also makes the automatic modes halve the
number of files; see [How the scheduler decides](#how-the-scheduler-decides).

The rename that gives a fully written file its final name counts its tries
separately, and for it a busy file gets the full number of tries, so that a
file held open by a virus scanner or an indexer is waited out instead of the
finished copy being thrown away.

**Rounds.** When a file has used up its tries and the last error was
transient or busy, the partial file is deleted and the file is set aside. It
is queued again when every other file has finished or failed, and is then
copied again from its first byte; only the progress readout carries over.
"Retry rounds for the whole task" is the number of rounds each file gets, the
first included: with the defaults a file is tried up to 3 × 10 times before it
is reported. A permanent error or a conflict ends the file at once, in any
round.

An error that follows earlier ones is reported with the first: "*first
error*; after *n* attempts, last failure: *last error*".

### The retry prompt

When a pass ends with failures that are not conflicts, a dialog titled "*n*
items could not be copied" (or "moved") lists up to six of them with a plain
reason; the raw error is in each row's tooltip. It offers:

* **Retry.** Exactly the failed items go through a new pass, each to its
  original destination.
* **Skip.** The failed items are left as they are and counted as skipped.
* **Cancel.** The task ends as cancelled. What was copied stays.

Conflicts are asked one file at a time, as described under
[When the destination exists](#when-the-destination-exists), and the answered
files go through the same new pass.

### How a retried item recognizes what is already there

A retried item is a file or a whole folder. A folder is walked again from its
top. When you choose **Retry** after an error, the engine first looks at the destination: when a file is
already there and matches the source by the [check mode](#check-modes), it
counts as copied and is not copied again (in a move, its source is then
deleted). A missing file is copied. A file that is there but does not match is
a conflict, and you are asked.

### Where failures are recorded

`%LOCALAPPDATA%\SumaFile\operations.jsonl` gets one line when a pass starts
and one when it completes, is cancelled or fails. The line of a failed pass
carries the message of its first failure.

## Check modes

"Decide a file is already up to date by" has four choices.

| Choice | Stored as | An existing destination file counts as already copied when |
| --- | --- | --- |
| Size and modification time | `size_and_mod_time` | it has the source's size and modification time |
| Size only | `size_only` | it has the source's size |
| Checksum | `checksum` | it has the source's size and both files, read in full, are identical |
| Always copy | `ignore_times` | the same as Checksum: both files, read in full, are identical |

**What the choice decides.**

* When you retry a failed item, a destination file that
  counts as already copied is left as it is and reported as done. With "Size
  only", a different file of the same size counts. "Always copy" does not
  force a copy here; it compares content like Checksum. An explicit Replace or
  Keep both choice always performs the requested action, even for two matching files.
* Checksum also verifies each copy. While a file is copied, a SHA-256 hash of
  the bytes read from the source is computed. After the last write the partial
  file is read back and hashed, and a difference fails the try as a transient
  error. In a move, the source is read once more and must still have that hash
  before it is deleted. Checksum turns copying in several streams off. The
  extra reads cost time, most of all on slow or cloud storage.

**What the choice does not decide.** It never makes a first pass skip a file.
A first pass does not compare the source with what is at the destination: an
existing file there is always a conflict, answered with Replace, Skip or Keep
both. The setting is not a "copy only what changed" switch.

## Progress readout

A row shows bytes done of total bytes, a percentage, the speed, the time
remaining and the name of a file in progress. While several files are copied
at once the line names one of them and counts the rest: "Movie A.mkv and 7
more files".

Details lists every file being copied (up to 32) under the folder it comes
from, with its own bytes. Folders and names are in alphabetical order, and the
destination is not repeated there; it is in the row's heading. Every line is
one row high. A folder path too long for its line keeps its root and its last
folders (`D:\…\Season 2\Extras\`), and a name too long keeps its start and
its end with the extension (`IMG_2025011…6789.jpg`). Hover over a
line for the whole path, or right-click it and choose **Copy path**.

* **Total** is the size of the selected files plus every file found so far
  inside the selected folders. It grows while folders are still being scanned,
  so the percentage can fall while bytes done rises.
* **Bytes done** is the full size of every finished file, plus the bytes
  written so far for files in progress, plus the bytes that had been written
  for files that then failed for good. A file that fails after its last byte
  stays counted, so the last update of a failed pass counts what was written.
* A file renamed by a same-drive move counts with its full size; a folder
  moved by one rename adds no bytes. A skipped file adds no bytes. A file
  recognized as already copied counts with its full size.
* The line of one file never goes down. A file set aside for another round
  keeps the bytes it had reached; the new round copies it from the first byte,
  and its line moves again once the new copy passes that point. While the file
  is set aside, its bytes are not in the task's bytes done, which is lower by
  that amount until the file is queued again.
* While a file is in its last steps (flush, verification, rename) the
  percentage can read "Finishing".
* The speed is a moving average of the change in bytes done.
* After Retry or a conflict answer, the readout counts only the items of the
  new pass.

## How the scheduler decides

These rules apply to the Automatic, Conservative and Maximum throughput modes.
Custom uses your file limit as one limit and measures nothing.

**It starts from the hardware.** Before anything is measured, the first number
of simultaneous files comes from the slower of the source and destination
drives, the mode, and whether the files are small (a batch of at least 16
files averaging 256 KiB or less) or large:

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
point on those routes. The batch is judged from the files found when copying
starts, 64 at most.

A hard disk on either end sets the pace, because parallel files make its head
seek. Windows is asked once per transfer whether each fixed disk has a seek
penalty; a disk that does not answer counts as not identified. A remembered
setting for the route still wins over this table, and your file ceiling and
buffer budget still cap it.

**It reads speed in short windows.** A measuring window closes after at least
one second once enough data has moved (eight buffers, and at least 16 MiB for
ordinary files), or after two seconds. Three windows that agree make a
reading, so a steady transfer reaches its first decision after three to six
seconds, and a test of more files takes one window longer. The noisier the
windows, the more of them a reading needs (up to twelve); a transfer too
erratic to average is left alone, and a test that cannot be read within 24
windows is undone. On a link too slow to fill a window, the window stretches
to five seconds. A window counts only when the lane was full for at least 85%
of it, and a window in which no bytes moved is ignored: an empty queue or a
long wait is not evidence for fewer files.

**It tests more files, then fewer.** A step up adds half the current number,
at least one. It is kept when throughput rises by at least 8%, or by more when
the readings are noisy. After a step up that did not pay, the setting is held
for four readings, and each further failed step doubles that hold (8, 16, 32,
up to 64 readings). The scheduler then tests half the number once and keeps
the lower number when it reaches at least 95% of the best speed.

**Small and large files get a lane each.** Files under 1 MiB and files of
1 MiB or more are queued separately and run side by side, each lane with its
own number of simultaneous files that adapts on its own. Together they never
exceed your file ceiling; the buffer budget and the bandwidth limit are
shared. When both lanes have work each gets at least one file, the file that
has waited longest is sent first, and a lane that runs out of work gives its
share to the other. Only one lane tests a new setting at a time. With both
lanes active the Details line shows, for example, "4 large + 12 small". Lanes
are off, and all files share one queue, when the mode is Custom or when you
choose a transfer order (by size or by name).

**The small-file lane counts files, not bytes.** For files under 1 MiB the
time goes into opening, creating and renaming each file (and, on a cloud
drive, fetching it), not into its bytes, and bytes per second swings with the
file sizes. So this lane is judged on completed files per second. Its window
closes after at least one second once 32 files have finished, or after two
seconds (stretching to five when files finish slowly). The large-file lane
and single-queue transfers read bytes per second.

Where a network share or an unidentified drive (a cloud mount, for example) is
at either end and no hard disk, removable drive or optical disc is involved,
that per-file wait happens off your machine, so the small-file lane leans
towards more files at once:

* It starts with 16 files in Automatic and 24 in Maximum throughput (2 in
  Conservative), instead of the table's 8 and 12 for a network share or 12 and
  16 for an unidentified drive. Each file in this lane holds one small buffer.
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

On every other route (solid-state drives on both ends, or a hard disk,
removable drive or optical disc at either end) the small-file lane keeps the
starting points in the table above and tests fewer files after any
unsuccessful step up.

**An I/O retry halves the lane at once**, in every window that sees one, so a
provider that starts throttling brings it down within seconds. The setting is
then held for four readings; each later bout of retries in the same transfer
doubles that hold (8, 16, up to 64 readings), so a throttling provider is not
pushed again straight away. This applies to both lanes. A busy file (a sharing
or lock violation) is not counted as a retry here.

**It watches write latency as an early warning.** Each write of 64 KiB or more
is timed. When the typical (median) write in a lane takes at least twice as
long as that lane's own baseline for three windows in a row, a running test of
more files is ended and the previous setting kept ("Write latency rose;
keeping the previous setting"). At a settled setting the scheduler holds
("Write latency rose; holding") and next checks whether fewer files are as
fast. Latency alone never reduces the number of files, and one slow write
(an antivirus scan, a cloud file being downloaded) does not move the median.

The limits protect resources; they do not promise the best possible speed. A
short task can finish before there is enough evidence to change anything.

On a mounted cloud drive SumaFile reads and writes through the mount. A file
is done for SumaFile when the mount has accepted it. The cloud client's cache
and its uploads are its own; SumaFile neither sees nor controls them, and the
speed it measures is the speed of the mount.

## Learned settings

When a task ends without a cancel and with no failed or conflicting item, each
lane saves the number of files that measured best. The record is keyed by a
hash of: copy or move; the identity of the source volumes and of the
destination volume, in that direction (a share's server and share name, a
mapped drive's remote path, or a local volume's letter, label and serial
number); and the average file size class (under 1 MiB, under 16 MiB, larger).
The small-file lane's files-per-second values are kept under a key of their
own, apart from values learned from bytes per second.

* Nothing is saved from a Custom task or from a task with a bandwidth limit.
* A record is used for 30 days after it was last saved, and at most 64 routes
  are kept; the oldest goes first.
* A remembered number is a starting point. It is measured again ("Rechecking
  remembered setting") and stays within the current ceiling.
* The record holds the hash, the number and a time. It holds no paths, names,
  credentials or file contents.

**Forget learned settings** in Transfer defaults empties the record. A task
that is running keeps what it has measured and can save again when it ends.

## Limits

* **Two tasks at a time.** Further tasks are **Queued**. A queued task whose
  destination overlaps that of a running task (the same folder, one inside
  the other, or the same item) waits until that task ends even when a slot is
  free, and its row says **Waiting**. Plugin and archive rows do not use these
  two slots.
* **File workers.** All tasks together run at most 32 files at once.
* **Copy buffers.** One task reserves at most its copy buffer budget (16–512
  MiB). All tasks together hold at most 512 MiB, buffers in use and cached
  buffers counted together; the cache holds at most 64 MiB and 32 buffers, and
  a buffer is reused only for a request of exactly its size. A buffer cut to
  the length of one small file is not cached, and a full cache drops its
  oldest buffers to keep the newest.
* **Per file.** A file reserves one buffer when it fits in one, three when it
  is larger, or one per stream, plus 128 KiB. A file that does not fit waits,
  and can appear in the Details list while it waits.
* **Bandwidth.** The limit is per task and counts the bytes actually copied.
  It is not a limit for the whole machine. The files being copied take turns
  at it, and one turn is at most a sixteenth of a second of the limit (never
  less than 64 KiB), so a large file and the small files beside it all keep
  moving however low the limit is.
* **Folder depth.** A folder 1,024 levels or more below a selected folder is
  not entered; it is reported and the rest continues.
* **Memory outside the buffers.** The limits above cover copy buffers only.
  Bookkeeping grows with the number of folders in the tree and with the number
  of selected items; a finished file inside a folder is dropped from the
  queue.

## Where settings and learned values are stored

All of it is in the `settings` table of `metadata.db` in SumaFile's
application data folder (`%APPDATA%\com.simplefile.desktop` on a new
installation; [Architecture](ARCHITECTURE.md) has the full rule).

| Key | Holds | Written |
| --- | --- | --- |
| `transfer.policy` | The defaults, as one JSON object | By Apply in Transfer defaults |
| `transfer.overrides.v1` | The settings of single tasks, by pass ID, each with the time it was saved; at most 32 | When a task with its own settings starts a pass and by Apply in Task transfer options; an entry is removed when its pass ends, by Use defaults, and 24 hours after it was saved |
| `transfer.learning.v1` | Learned numbers of files per route | By the service when a task ends cleanly; emptied by Forget learned settings |

The fields of `transfer.policy`, in the order of the settings table: `mode`
(`automatic`, `conservative`, `maximum`, or `manual` for Custom),
`manualConcurrency`, `bandwidthMib`, `checkMode`, `maxConcurrency`,
`checkers`, `maxBacklog`, `orderBy` (`default`, `size_asc`, `size_desc`,
`name`), `bufferSizeKib`, `memoryBudgetMb`, `multiThreadCutoffMib`,
`multiThreadStreams`, `inplace`, `preallocate`, `flushOnClose`,
`lowLevelRetries`, `highLevelRetries` and `retrySleepMs` (milliseconds). The
service gives a missing field its default, reads an unknown mode, order or
check mode as the default, and brings a number outside its range to the
nearest limit.

## Path handling

* **Repeated separators.** Before it plans a transfer, the service rewrites
  every source and destination path so that a run of `\` or `/` after the root
  becomes one separator: `V:\\sync\Movies` becomes `V:\sync\Movies`. The
  leading `\\` of a UNC path and a `\\?\` or `\\.\` prefix are kept. Files can
  be created and written under a doubled path, but the rename that gives a
  file its final name can be refused there ("The parameter is incorrect").
* **Long paths.** A partial file's name is 35 characters longer than the
  file's own name, and its record's name 41 characters longer, so a
  destination close to 260 characters passes that length during the copy. The
  rename converts any path of 248 characters or more to the extended form
  (`\\?\`, or `\\?\UNC\` for a share), as the service's other file calls do
  for long paths.
* **A folder into itself.** Copying or moving a folder into itself or one of
  its subfolders is refused.

## FTP, FTPS and SFTP

FTP, FTPS and SFTP uploads and downloads run in the bundled FTP/SFTP plugin
(`com.sumafile.remote`), not in the scheduler described above, and none of the
settings above apply to them. The plugin has eight settings of its own, under
Settings > Plugins > FTP/SFTP connections and in the manager's Transfer
options dialog: "Bandwidth limit (MB/s, 0 = unlimited)", "Decide a file is
already up to date by", "Write downloads directly to the destination instead
of a temporary file", "Pre-allocate local download space", "Flush local
downloads to disk before finishing each file", "Retry a failing file up to"
(10), "Retry rounds per failed file" (3) and "Wait before retrying
(milliseconds)" (1000). There are no settings for concurrency or buffers: a
remote session transfers one file at a time, with buffers sized by the
protocol.

A download writes its local file with the same partial file, ownership record,
rename and recovery as a local copy. A retry reconnects and transfers the file
again from its first byte. In Checksum mode the plugin reads the remote file
once more to verify it. On the upgrade from a version without the plugin, the
values in `transfer.policy` that differed from the defaults were copied into
the plugin's settings once; remote transfers do not read that row afterwards.
See [Plugins](PLUGINS.md).
