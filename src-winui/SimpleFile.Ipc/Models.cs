using System.Text.Json.Serialization;

namespace SimpleFile.Ipc;

public sealed class FileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }

    [JsonPropertyName("is_symlink")]
    public bool IsSymlink { get; set; }

    [JsonPropertyName("is_hidden")]
    public bool IsHidden { get; set; }

    [JsonPropertyName("is_system")]
    public bool IsSystem { get; set; }

    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    [JsonPropertyName("modified")]
    public string Modified { get; set; } = "";

    [JsonPropertyName("extension")]
    public string Extension { get; set; } = "";

    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }

    [JsonPropertyName("symlink_target")]
    public string? SymlinkTarget { get; set; }

    /// <summary>
    /// Retired: the service always leaves it null. Git status moved to the
    /// <c>com.sumafile.git</c> plugin's row decorations in phase 4; the property
    /// stays so the v1 wire format and the binary listing frame keep their shape.
    /// </summary>
    [JsonPropertyName("git_status")]
    public string? GitStatus { get; set; }

    [JsonPropertyName("itemCount")]
    public ulong? ItemCount { get; set; }

    /// <summary>Creation time in the form of <see cref="Modified"/>, or empty when unknown.</summary>
    [JsonPropertyName("created")]
    public string Created { get; set; } = "";

    /// <summary>Last access time in the form of <see cref="Modified"/>, or empty when unknown.</summary>
    [JsonPropertyName("accessed")]
    public string Accessed { get; set; } = "";

    /// <summary>Explorer's attribute letters (R H S A C E O T, L for a reparse point), or empty.</summary>
    [JsonPropertyName("attributes")]
    public string Attributes { get; set; } = "";

    /// <summary>
    /// The lazy column values read for this entry so far, by fact kind (see <c>get_entry_facts</c>); null until one
    /// arrives. Replaced whole on every write, so a reader never sees a dictionary being changed.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, EntryFact>? Facts { get; set; }

    /// <summary>The fact of <paramref name="kind"/> for this entry, in whatever state it settled, or null when none was read.</summary>
    public EntryFact? FactFor(string kind) =>
        Facts is { } facts && facts.TryGetValue(kind, out var fact) ? fact : null;
}

public sealed class DirectoryListing
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("entries")]
    public List<FileEntry> Entries { get; set; } = [];

    [JsonPropertyName("is_network")]
    public bool IsNetwork { get; set; }
}

/// <summary>
/// The folder a pane shows and the order and filter it wants from the service-held view of it
/// (<c>open_listing_view</c>). The service sorts and filters; the pane reads the rows in pages.
/// </summary>
public sealed class ListingViewRequest
{
    /// <summary>The pane's view: opening it again replaces (or, for the same folder, re-sorts) the pane's view.</summary>
    public required string ViewId { get; init; }

    public required string Path { get; init; }

    /// <summary><c>name</c>, <c>size</c>, <c>modified</c>, <c>extension</c> or <c>type</c>.</summary>
    public string SortBy { get; init; } = "name";

    public bool SortAscending { get; init; } = true;

    public bool FoldersFirst { get; init; } = true;

    /// <summary>False leaves out Hidden and System entries and names that start with a dot.</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>A case-insensitive substring of the name, or blank.</summary>
    public string Filter { get; init; } = "";

    /// <summary>The culture names sort in, for example <c>en-US</c>.</summary>
    public string Locale { get; init; } = "";

    /// <summary>The rows of the first page.</summary>
    public uint PageSize { get; init; }

    /// <summary>Enumerate the folder again even when the view already shows it.</summary>
    public bool Reload { get; init; }

    /// <summary>
    /// Only re-sort and re-filter the view the pane already has of this folder. The service enumerates nothing and
    /// fails when the view shows another folder; unlike a navigation, the request does not supersede an open that is
    /// still under way.
    /// </summary>
    public bool Reuse { get; init; }

    /// <summary>This request as one that only re-sorts the view the pane already has (<see cref="Reuse"/>).</summary>
    public ListingViewRequest ForTheViewShown() => new()
    {
        ViewId = ViewId,
        Path = Path,
        SortBy = SortBy,
        SortAscending = SortAscending,
        FoldersFirst = FoldersFirst,
        IncludeHidden = IncludeHidden,
        Filter = Filter,
        Locale = Locale,
        PageSize = PageSize,
        Reuse = true,
    };

    /// <summary>True when both requests ask for the same rows in the same order.</summary>
    public bool SameViewAs(ListingViewRequest? other) =>
        other is not null
        && string.Equals(SortBy, other.SortBy, StringComparison.OrdinalIgnoreCase)
        && SortAscending == other.SortAscending
        && FoldersFirst == other.FoldersFirst
        && IncludeHidden == other.IncludeHidden
        && string.Equals(Filter.Trim(), other.Filter.Trim(), StringComparison.Ordinal)
        && string.Equals(Locale, other.Locale, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Rows <c>Offset..Offset + Entries.Count</c> of the <c>Total</c> rows a listing view shows, as of <c>Revision</c>.</summary>
public sealed class ListingViewPage
{
    [JsonPropertyName("view_id")]
    public string ViewId { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("is_network")]
    public bool IsNetwork { get; set; }

    [JsonPropertyName("total")]
    public uint Total { get; set; }

    [JsonPropertyName("revision")]
    public ulong Revision { get; set; }

    [JsonPropertyName("offset")]
    public uint Offset { get; set; }

    [JsonPropertyName("entries")]
    public List<FileEntry> Entries { get; set; } = [];
}

/// <summary>
/// One row change of a listing view. <c>Kind</c> is <c>remove</c>, <c>insert</c> or <c>update</c>;
/// <c>Index</c> is the row, counted after the changes before this one in the same delta.
/// </summary>
public sealed class ListingViewChange
{
    public const string Remove = "remove";
    public const string Insert = "insert";
    public const string Update = "update";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("index")]
    public uint Index { get; set; }

    [JsonPropertyName("entry")]
    public FileEntry? Entry { get; set; }

    /// <summary>
    /// On the <c>insert</c> or <c>update</c> of an entry that was renamed inside the folder: the name it had. The
    /// client makes it the path it had, as it does for the entry's own path, so what is held for that path (the
    /// selection) moves to the entry.
    /// </summary>
    [JsonPropertyName("renamed_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RenamedFrom { get; set; }
}

/// <summary>
/// What changed in a listing view between <c>BaseRevision</c> and <c>Revision</c>. <c>Reset</c> means
/// too much changed to list: the rows must be read again.
/// </summary>
public sealed class ListingViewDelta
{
    [JsonPropertyName("view_id")]
    public string ViewId { get; set; } = "";

    [JsonPropertyName("base_revision")]
    public ulong BaseRevision { get; set; }

    [JsonPropertyName("revision")]
    public ulong Revision { get; set; }

    [JsonPropertyName("total")]
    public uint Total { get; set; }

    [JsonPropertyName("reset")]
    public bool Reset { get; set; }

    [JsonPropertyName("changes")]
    public List<ListingViewChange> Changes { get; set; } = [];
}

public sealed class ListDirectoryOptions
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "full";

    [JsonPropertyName("finalEntries")]
    public bool? FinalEntries { get; init; }

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; init; }

    [JsonPropertyName("sortAscending")]
    public bool? SortAscending { get; init; }

    [JsonPropertyName("filter")]
    public string? Filter { get; init; }

    [JsonPropertyName("includeHidden")]
    public bool? IncludeHidden { get; init; }
}

public sealed class DirectoryListingChunk
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("entries")]
    public List<FileEntry> Entries { get; set; } = [];

    [JsonPropertyName("chunk_index")]
    public uint ChunkIndex { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("is_network")]
    public bool IsNetwork { get; set; }
}

public sealed class DirectoryListingChunkNotification
{
    [JsonPropertyName("requestId")]
    public int RequestId { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("entries")]
    public List<FileEntry> Entries { get; set; } = [];

    [JsonPropertyName("chunk_index")]
    public uint ChunkIndex { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("is_network")]
    public bool IsNetwork { get; set; }

    public DirectoryListingChunk ToChunk()
    {
        return new DirectoryListingChunk
        {
            Path = Path,
            Parent = Parent,
            Entries = Entries,
            ChunkIndex = ChunkIndex,
            Done = Done,
            IsNetwork = IsNetwork,
        };
    }
}

public sealed class DriveInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("drive_type")]
    public string DriveType { get; set; } = "";

    [JsonPropertyName("file_system")]
    public string? FileSystem { get; set; }

    [JsonPropertyName("total_space")]
    public ulong TotalSpace { get; set; }

    [JsonPropertyName("free_space")]
    public ulong FreeSpace { get; set; }

    [JsonPropertyName("remote_path")]
    public string? RemotePath { get; set; }

    [JsonPropertyName("drive_status")]
    public string DriveStatus { get; set; } = "";

    [JsonPropertyName("status_detail")]
    public string? StatusDetail { get; set; }
}

public sealed class ProgressUpdate
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("operation_type")]
    public string OperationType { get; set; } = "";

    [JsonPropertyName("current")]
    public ulong Current { get; set; }

    [JsonPropertyName("total")]
    public ulong Total { get; set; }

    [JsonPropertyName("current_files")]
    public ulong CurrentFiles { get; set; }

    [JsonPropertyName("total_files")]
    public ulong TotalFiles { get; set; }

    [JsonPropertyName("current_item")]
    public string CurrentItem { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Bytes written to destinations in this pass so far, a count that only grows: a file copied again is counted
    /// again. The speed is measured on it. Files skipped, found already at the destination or moved by renaming them
    /// count toward <see cref="Current"/>, not toward it.
    /// </summary>
    [JsonPropertyName("transferred")]
    public ulong Transferred { get; set; }
}

public sealed class ActiveFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
    [JsonPropertyName("current")]
    public ulong Current { get; set; }
    [JsonPropertyName("total")]
    public ulong Total { get; set; }
}

public sealed class TuningUpdate
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = "";
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "automatic";
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; }
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";
    [JsonPropertyName("active_files")]
    public ActiveFile[] ActiveFiles { get; set; } = [];
}

public sealed class FileChangeEvent
{
    /// <summary>
    /// The kind that reports a watched folder itself, <see cref="Path"/>, as no longer there: deleted, renamed or
    /// moved, or its volume left. The service has stopped watching it.
    /// </summary>
    public const string Gone = "gone";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>
    /// <c>create</c>, <c>modify</c>, <c>remove</c> or <c>rename</c> for an entry of a watched folder;
    /// <c>modify</c> with the path of a watched folder itself when more changed there than is listed; or
    /// <see cref="Gone"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
}

public sealed class SearchResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }

    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    [JsonPropertyName("modified")]
    public string Modified { get; set; } = "";

    [JsonPropertyName("extension")]
    public string Extension { get; set; } = "";

    [JsonPropertyName("match_type")]
    public string MatchType { get; set; } = "";
}

public sealed class SearchResultsBatchEvent
{
    [JsonPropertyName("search_id")]
    public string? SearchId { get; set; }

    [JsonPropertyName("results")]
    public SearchResult[] Results { get; set; } = [];
}

public sealed class SearchCompleteEvent
{
    [JsonPropertyName("search_id")]
    public string? SearchId { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public sealed class SearchOptions
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("search_path")]
    public string SearchPath { get; set; } = "";

    [JsonPropertyName("case_sensitive")]
    public bool CaseSensitive { get; set; }

    [JsonPropertyName("include_hidden")]
    public bool IncludeHidden { get; set; }

    [JsonPropertyName("file_types")]
    public string[]? FileTypes { get; set; }

    [JsonPropertyName("max_results")]
    public int? MaxResults { get; set; }

    [JsonPropertyName("max_depth")]
    public int? MaxDepth { get; set; }

    [JsonPropertyName("search_id")]
    public string? SearchId { get; set; }

    [JsonPropertyName("content_search")]
    public bool ContentSearch { get; set; }

    [JsonPropertyName("min_size")]
    public ulong? MinSize { get; set; }

    [JsonPropertyName("max_size")]
    public ulong? MaxSize { get; set; }

    [JsonPropertyName("date_after")]
    public string? DateAfter { get; set; }

    [JsonPropertyName("date_before")]
    public string? DateBefore { get; set; }
}

public sealed class TreeNode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
    [JsonPropertyName("has_children")]
    public bool HasChildren { get; set; }
    [JsonPropertyName("children")]
    public List<TreeNode> Children { get; set; } = [];
}

public sealed class FolderMetrics
{
    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    [JsonPropertyName("item_count")]
    public ulong ItemCount { get; set; }

    [JsonPropertyName("subdirectories")]
    public List<TreeNode> Subdirectories { get; set; } = [];

    /// <summary>Files at every depth below the folder.</summary>
    [JsonPropertyName("total_files")]
    public ulong TotalFiles { get; set; }

    /// <summary>Folders at every depth below the folder.</summary>
    [JsonPropertyName("total_folders")]
    public ulong TotalFolders { get; set; }
}

public sealed class RenameRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
    [JsonPropertyName("new_name")]
    public string NewName { get; set; } = "";
}

public sealed class TransferResult
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("changes")]
    public TransferChanges? Changes { get; set; }

    [JsonIgnore]
    public bool CanUndo => Changes is null || Changes.Files.Length != 0
        || Changes.CreatedDirectories.Length != 0 || Changes.RemovedSourceDirectories.Length != 0;
}

public sealed class TransferChanges
{
    [JsonPropertyName("files")]
    public string[] Files { get; set; } = [];
    [JsonPropertyName("created_dirs")]
    public string[] CreatedDirectories { get; set; } = [];
    [JsonPropertyName("removed_source_dirs")]
    public string[] RemovedSourceDirectories { get; set; } = [];
    [JsonPropertyName("streamed_paths")]
    public int StreamedPaths { get; set; }
}

public sealed class TransferChangeChunk
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = "";
    [JsonPropertyName("result_index")]
    public int ResultIndex { get; set; }
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";
    [JsonPropertyName("paths")]
    public string[] Paths { get; set; } = [];
}

/// <summary>
/// One path of a call that goes on past a path it cannot handle (<c>move_to_trash_each</c>,
/// <c>restore_recycle_bin_each</c>): the call answers one for each path, in the order given.
/// </summary>
public sealed class PathOutcome
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>What the path became: its Recycle Bin item, or where it was restored. Null when not known.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    /// <summary>Why the path was not done; null when it was.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonIgnore]
    public bool Done => Error is null;
}

public sealed class TransferFailure
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>The exact path the item was headed for; a targeted retry sends it back verbatim.</summary>
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    /// <summary>"conflict" (the user decides), "cancelled", or "error".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "error";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonIgnore]
    public bool IsConflict => string.Equals(Kind, "conflict", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One file to transfer again to an exact destination with its own conflict action.</summary>
public sealed class TransferItemRequest
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    /// <summary>"error", "skip", "replace", "keep-both", or "create-only" for exclusive replay.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "error";
}

public sealed class TransferBatchOutcome
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("committed")]
    public TransferResult[] Committed { get; set; } = [];

    [JsonPropertyName("skipped")]
    public TransferResult[] Skipped { get; set; } = [];

    [JsonPropertyName("failed")]
    public TransferFailure[] Failed { get; set; } = [];

    [JsonPropertyName("pending")]
    public string[] Pending { get; set; } = [];
}

public sealed class FilePreview
{
    [JsonPropertyName("file_type")]
    public string FileType { get; set; } = "";

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }
}

public sealed class ThumbnailResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("data")]
    public byte[]? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>An encoded picture for the preview pane, from the file service's media process.</summary>
public sealed class EncodedImage
{
    /// <summary><c>jpeg</c> or <c>png</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("width")]
    public uint Width { get; set; }

    [JsonPropertyName("height")]
    public uint Height { get; set; }

    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// The video thumbnails the file service's video process has decoded since the service started. A thumbnail
/// that came from the disk cache was not decoded and is not counted.
/// </summary>
public sealed class MediaStatus
{
    /// <summary>Thumbnails whose frame the graphics card decoded.</summary>
    [JsonPropertyName("videoHardware")]
    public ulong VideoHardware { get; set; }

    /// <summary>Thumbnails whose frame the processor decoded.</summary>
    [JsonPropertyName("videoSoftware")]
    public ulong VideoSoftware { get; set; }

    /// <summary>
    /// Why the processor last decoded one: <c>turned_off</c>, <c>no_device</c>, <c>no_hardware_decoder</c> or
    /// <c>hardware_failed</c>. Null when it never has.
    /// </summary>
    [JsonPropertyName("lastSoftwareReason")]
    public string? LastSoftwareReason { get; set; }
}

public sealed class Checksums
{
    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

public sealed class ImageMetadata
{
    [JsonPropertyName("width")]
    public uint Width { get; set; }

    [JsonPropertyName("height")]
    public uint Height { get; set; }

    [JsonPropertyName("exif")]
    public List<string[]> Exif { get; set; } = [];
}

public sealed class FileMetadata
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("fields")]
    public List<string[]> Fields { get; set; } = [];
}

public sealed class DiffRow
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("left_line")]
    public int? LeftLine { get; set; }

    [JsonPropertyName("right_line")]
    public int? RightLine { get; set; }

    [JsonPropertyName("left_text")]
    public string? LeftText { get; set; }

    [JsonPropertyName("right_text")]
    public string? RightText { get; set; }
}

public sealed class BinaryDiffRow
{
    [JsonPropertyName("offset")]
    public ulong Offset { get; set; }

    [JsonPropertyName("left_hex")]
    public string LeftHex { get; set; } = "";

    [JsonPropertyName("right_hex")]
    public string RightHex { get; set; } = "";

    [JsonPropertyName("left_ascii")]
    public string LeftAscii { get; set; } = "";

    [JsonPropertyName("right_ascii")]
    public string RightAscii { get; set; } = "";

    [JsonPropertyName("different")]
    public bool Different { get; set; }
}

public sealed class FileComparison
{
    [JsonPropertyName("left_path")]
    public string LeftPath { get; set; } = "";

    [JsonPropertyName("right_path")]
    public string RightPath { get; set; } = "";

    [JsonPropertyName("left_name")]
    public string LeftName { get; set; } = "";

    [JsonPropertyName("right_name")]
    public string RightName { get; set; } = "";

    [JsonPropertyName("left_size")]
    public ulong LeftSize { get; set; }

    [JsonPropertyName("right_size")]
    public ulong RightSize { get; set; }

    [JsonPropertyName("identical")]
    public bool Identical { get; set; }

    [JsonPropertyName("added")]
    public int Added { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    [JsonPropertyName("changed")]
    public int Changed { get; set; }

    [JsonPropertyName("comparison_type")]
    public string ComparisonType { get; set; } = "text";

    [JsonPropertyName("compared_bytes")]
    public ulong? ComparedBytes { get; set; }

    [JsonPropertyName("different_bytes")]
    public ulong? DifferentBytes { get; set; }

    [JsonPropertyName("first_difference")]
    public ulong? FirstDifference { get; set; }

    [JsonPropertyName("binary_rows_truncated")]
    public bool BinaryRowsTruncated { get; set; }

    [JsonPropertyName("rows")]
    public List<DiffRow> Rows { get; set; } = [];

    [JsonPropertyName("binary_rows")]
    public List<BinaryDiffRow> BinaryRows { get; set; } = [];
}

public sealed class ArchiveEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }

    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    [JsonPropertyName("compressed_size")]
    public ulong CompressedSize { get; set; }
}

public sealed class ArchiveInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("entries")]
    public List<ArchiveEntry> Entries { get; set; } = [];

    [JsonPropertyName("unsafe_entries")]
    public List<string> UnsafeEntries { get; set; } = [];

    [JsonPropertyName("total_size")]
    public ulong TotalSize { get; set; }

    [JsonPropertyName("compressed_size")]
    public ulong CompressedSize { get; set; }
}

public sealed class ArchiveFormatCapability
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("extension")]
    public string Extension { get; set; } = "";

    [JsonPropertyName("can_list")]
    public bool CanList { get; set; }

    [JsonPropertyName("can_extract")]
    public bool CanExtract { get; set; }

    [JsonPropertyName("can_create")]
    public bool CanCreate { get; set; }

    [JsonPropertyName("can_modify")]
    public bool CanModify { get; set; }

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class ArchiveCapabilities
{
    [JsonPropertyName("formats")]
    public List<ArchiveFormatCapability> Formats { get; set; } = [];
}

public sealed class Tag
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "";
}

public sealed class SmartFolder
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("search_options")] public SearchOptions SearchOptions { get; set; } = new();
}

public sealed class CleanupFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public ulong Size { get; set; }
}

public sealed class DuplicateGroup
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
}

public sealed class CleanupResult
{
    [JsonPropertyName("large_files")] public List<CleanupFile> LargeFiles { get; set; } = [];
    [JsonPropertyName("duplicates")] public List<DuplicateGroup> Duplicates { get; set; } = [];
    [JsonPropertyName("scanned_files")] public ulong ScannedFiles { get; set; }
}

public sealed class DuplicateScanOptions
{
    [JsonPropertyName("minSize")] public ulong? MinSize { get; set; }
    [JsonPropertyName("partialHashBytes")] public ulong? PartialHashBytes { get; set; }
    [JsonPropertyName("maxDepth")] public int? MaxDepth { get; set; }
    [JsonPropertyName("excludePatterns")] public string[]? ExcludePatterns { get; set; }
    [JsonPropertyName("networkMode")] public bool? NetworkMode { get; set; }
}

public sealed class DuplicateCheckFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public ulong Size { get; set; }
    [JsonPropertyName("modified")] public string Modified { get; set; } = "";
}

public sealed class DuplicateCheckGroup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public ulong Size { get; set; }
    [JsonPropertyName("files")] public List<DuplicateCheckFile> Files { get; set; } = [];
    [JsonPropertyName("wasted_bytes")] public ulong WastedBytes { get; set; }
}

public sealed class DuplicateCheckResult
{
    [JsonPropertyName("groups")] public List<DuplicateCheckGroup> Groups { get; set; } = [];
    [JsonPropertyName("scanned_files")] public ulong ScannedFiles { get; set; }
    [JsonPropertyName("candidate_files")] public ulong CandidateFiles { get; set; }
    [JsonPropertyName("hashed_files")] public ulong HashedFiles { get; set; }
    [JsonPropertyName("skipped_files")] public ulong SkippedFiles { get; set; }
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
    [JsonPropertyName("total_reclaimable_bytes")] public ulong TotalReclaimableBytes { get; set; }
}

public sealed class AppAboutInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("authors")] public string Authors { get; set; } = "";
    [JsonPropertyName("repository")] public string Repository { get; set; } = "";
}

public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("installable")] public bool Installable { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public ulong? Size { get; set; }
}

/// <summary>One lazy-column value for one entry; see <c>get_entry_facts</c>.</summary>
public sealed class EntryFact
{
    public const string Ready = "ready";
    public const string None = "none";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>ready, none, unsupported or failed.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>What the column sorts by: pixels, seconds, pages, bytes, or unix milliseconds for a date.</summary>
    [JsonPropertyName("sort")]
    public double? Sort { get; set; }
}

public sealed class EntryFactsBatch
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("facts")]
    public List<EntryFact> Facts { get; set; } = [];
}

public sealed class EntryFactsResult
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("settled")]
    public uint Settled { get; set; }
}
