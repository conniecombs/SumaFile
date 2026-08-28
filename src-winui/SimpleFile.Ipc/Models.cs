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

    [JsonPropertyName("git_status")]
    public string? GitStatus { get; set; }

    [JsonPropertyName("itemCount")]
    public ulong? ItemCount { get; set; }
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
}

public sealed class FileChangeEvent
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

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
    public string? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
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

    [JsonPropertyName("rows")]
    public List<DiffRow> Rows { get; set; } = [];
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

public sealed class RarInstallPlan
{
    [JsonPropertyName("confirmation_token")] public string ConfirmationToken { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("installer_path")] public string InstallerPath { get; set; } = "";
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
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

public sealed class GitStatus
{
    [JsonPropertyName("is_repo")] public bool IsRepo { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("modified")] public int Modified { get; set; }
    [JsonPropertyName("staged")] public int Staged { get; set; }
    [JsonPropertyName("untracked")] public int Untracked { get; set; }
    [JsonPropertyName("ahead")] public int Ahead { get; set; }
    [JsonPropertyName("behind")] public int Behind { get; set; }
}
