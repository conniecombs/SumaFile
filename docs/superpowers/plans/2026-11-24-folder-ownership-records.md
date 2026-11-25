# One Ownership Record per Destination Folder

**Goal:** Remove the per-file cost of ownership records, the largest fixed
cost of copying many small files, without weakening what recovery may delete.

**Spec:** the Transfers section of [docs/ROADMAP.md](../../ROADMAP.md);
behaviour is described in
[docs/TRANSFER_TUNING.md](../../TRANSFER_TUNING.md#how-a-file-is-copied).

## Decisions made by the owner

1. **Where the shared record lives:** one per destination folder, shared by
   the workers of a pass. Not one per worker at the destination root.
2. **Durability of a file's line:** never forced to disk when it is written.
   The line that records where a replaced file is moved aside to is still
   forced to disk first.

## Approaches considered

| Approach | For | Against |
| --- | --- | --- |
| One record per destination folder (chosen) | A record stays beside the files it names, so recovery keeps handling the folder it visits; smallest change to the code that deletes files | A folder that receives one file still costs a record; a large folder needs its record replaced when full |
| One record per worker at the destination root | A fixed number of records per pass whatever the tree | A record names partial files in subfolders; recovery would have to look in parent folders; a subfolder reached by another path is separated from its record |
| One record per worker per folder | No sharing between workers | With 32 workers and small folders each worker makes its own record in each folder, and the gain disappears |
| No per-file line, ownership proved by the hash in the partial file's name | Nothing written per file | Gives up the check that the file found is the very file recorded, and measured no faster than a line |

## Design

- `FolderRecords` (in `sumafile-transfer/src/journal.rs`) belongs to a pass.
  `begin` creates the partial file and appends one line, a version 2 record,
  to the record of the file's folder:
  `.sumafile-transfer.<16 hex>.<number>.sumafile-partial.state`.
- A line holds what a file's own record holds. `recover` is unchanged and is
  run for every line; a line whose file has its final name is a no-op, so a
  line needs no ending.
- A record is replaced by a fresh one at 256 KiB and deleted when the last
  lease on it is gone. A pass keeps the records of the 64 folders it used
  last open.
- A lease that ends without `finish` or a clean `abort` marks its record to
  be kept and is remembered by the pass, so a later try of the same file can
  take the partial file over. A later pass of the same task recovers the
  folder when it finds the partial file in place.
- `StagingLease::begin` and `record_created` keep a file's own record
  (version 1): files written directly to the destination, links, and the
  remote plugin's downloads.
- `recover_directory` reads both versions. A build without folder records
  reads the last line, sees a version that is not 1, and leaves the record
  alone.

## Measurements

Windows 11, 3,000 files of 4 KiB, release build of the engine, automatic
concurrency, best of three, files per second.

| Drive | Folders | Flush each file | Record per file | Folder record |
| --- | ---: | --- | ---: | ---: |
| NVMe SSD | 1 | on (default) | 1,199 | 2,139 |
| NVMe SSD | 1 | off | 1,354 | 2,623 |
| NVMe SSD | 300 | on (default) | 1,850 | 3,480 |
| NVMe SSD | 300 | off | 2,124 | 3,871 |
| RAM disk | 1 | on (default) | 3,425 | 5,345 |
| RAM disk | 1 | off | 3,804 | 7,445 |
| RAM disk | 300 | on (default) | 4,427 | 8,443 |
| RAM disk | 300 | off | 5,049 | 11,488 |

## Tests

- `journal.rs`: one record for all the files of a folder; recovery after a
  pass that stopped (published files kept, partial files removed); a partial
  file that is now another file is kept with its record while the record's
  other files are cleaned up; a replaced file is restored; a full record is
  replaced and removed; the limit on open records; a retry in the same pass
  and in a later pass; an unrecorded partial file is not taken over; the
  version an older build sees; a record that names files elsewhere is
  ignored. The tests of a file's own record are unchanged.
- `progress.rs`: a pass of 300 files, eight at a time, never has more than
  one record in the destination folder and leaves none.

## Left open

On the [roadmap](../../ROADMAP.md): the record per worker at the destination
root for trees of small folders, and cleaning up the partial file a power
loss can leave without a line.
