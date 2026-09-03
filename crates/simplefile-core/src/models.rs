use serde::{Deserialize, Serialize};

// ============================================================================
// File System Types
// ============================================================================

/// Represents a file or directory entry
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct FileEntry {
    pub name: String,
    pub path: String,
    pub is_dir: bool,
    pub is_symlink: bool,
    /// Windows Hidden attribute, or a leading-dot name.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_hidden: bool,
    /// Windows System attribute (protected operating-system files).
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_system: bool,
    pub size: u64,
    pub modified: String,
    pub extension: String,
    /// Unix permission string like "rwxr-xr-x" (None on Windows or if unavailable)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub permissions: Option<String>,
    /// Symlink target path (None if not a symlink)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub symlink_target: Option<String>,
    /// Git status (e.g. "modified", "untracked", "added", "deleted")
    #[serde(skip_serializing_if = "Option::is_none")]
    pub git_status: Option<String>,
}

/// Represents the result of a directory listing
#[derive(Debug, Serialize, Deserialize)]
pub struct DirectoryListing {
    pub path: String,
    pub parent: Option<String>,
    pub entries: Vec<FileEntry>,
    /// True when the listed path is a UNC share or a mapped network drive.
    #[serde(default)]
    pub is_network: bool,
}

/// Progressive listing chunk streamed to the frontend while enumeration runs.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DirectoryListingChunk {
    pub path: String,
    pub parent: Option<String>,
    pub entries: Vec<FileEntry>,
    pub chunk_index: u32,
    pub done: bool,
    pub is_network: bool,
}

/// Progress update for long-running operations
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct ProgressUpdate {
    pub operation_id: String,
    pub operation_type: String,
    pub current: u64,
    pub total: u64,
    pub current_files: u64,
    pub total_files: u64,
    pub current_item: String,
    pub status: String, // "running", "completed", "error", "cancelled"
    pub error: Option<String>,
}

/// File system change event
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct FileChangeEvent {
    pub path: String,
    pub kind: String, // "create", "modify", "remove", "rename"
}

// ============================================================================
// Drive Types
// ============================================================================

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct DriveInfo {
    pub name: String,
    pub path: String,
    pub drive_type: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file_system: Option<String>,
    pub total_space: u64,
    pub free_space: u64,
    pub remote_path: Option<String>,
    pub drive_status: String,
    pub status_detail: Option<String>,
}

// ============================================================================
// Tree / Directory Types
// ============================================================================

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct TreeNode {
    pub name: String,
    pub path: String,
    pub has_children: bool,
    pub children: Vec<TreeNode>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct FolderMetrics {
    pub size: u64,
    pub item_count: u64,
    pub subdirectories: Vec<TreeNode>,
}

// ============================================================================
// Search Types
// ============================================================================

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct SearchResult {
    pub name: String,
    pub path: String,
    pub is_dir: bool,
    pub size: u64,
    pub modified: String,
    pub extension: String,
    pub match_type: String,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct SearchOptions {
    pub query: String,
    pub search_path: String,
    pub case_sensitive: bool,
    pub include_hidden: bool,
    pub file_types: Option<Vec<String>>,
    pub max_results: Option<usize>,
    pub max_depth: Option<usize>,
    /// Optional unique identifier for this search. When set, the
    /// backend will track cancellation using this ID and allow
    /// multiple searches to run concurrently. If None, a random ID
    /// may be assigned on the frontend.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub search_id: Option<String>,
    /// Enable full-text search of text file contents in addition to
    /// filename matching. Note: content search may be slow on large
    /// trees and should be used sparingly.
    #[serde(default)]
    pub content_search: bool,
    /// Minimum file size (in bytes) to include in results. When set,
    /// files smaller than this will be skipped.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub min_size: Option<u64>,
    /// Maximum file size (in bytes) to include in results. When set,
    /// files larger than this will be skipped.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_size: Option<u64>,
    /// Only include files modified on or after this date (ISO 8601
    /// format, e.g. "2024-01-01T00:00:00"). When None, no lower
    /// bound is applied.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub date_after: Option<String>,
    /// Only include files modified on or before this date (ISO 8601
    /// format). When None, no upper bound is applied.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub date_before: Option<String>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct SmartFolder {
    pub id: String,
    pub name: String,
    pub icon: Option<String>,
    pub search_options: SearchOptions,
}

#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct Tag {
    pub id: i64,
    pub name: String,
    pub color: String,
}

// ============================================================================
// Disk Cleanup Types
// ============================================================================

/// Represents a group of duplicate files. All files in this group share
/// the same content hash. The `hash` field contains the computed checksum
/// (e.g. SHA-256) used to detect duplicates.
#[derive(Debug, Serialize, Deserialize)]
pub struct DuplicateGroup {
    pub hash: String,
    pub files: Vec<String>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct CleanupFile {
    pub path: String,
    pub size: u64,
}

/// Result of a disk cleanup scan. The `large_files` vector lists files
/// exceeding the requested size threshold along with their sizes. The
/// `duplicates` vector contains groups of duplicate files (excluding the
/// first occurrence in each group).
#[derive(Debug, Serialize, Deserialize)]
pub struct CleanupResult {
    pub large_files: Vec<CleanupFile>,
    pub duplicates: Vec<DuplicateGroup>,
    #[serde(default)]
    pub scanned_files: u64,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct DuplicateCheckFile {
    pub path: String,
    pub name: String,
    pub size: u64,
    pub modified: String,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct DuplicateCheckGroup {
    pub id: String,
    pub hash: String,
    pub size: u64,
    pub wasted_bytes: u64,
    pub files: Vec<DuplicateCheckFile>,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct DuplicateCheckResult {
    pub groups: Vec<DuplicateCheckGroup>,
    pub scanned_files: u64,
    pub candidate_files: u64,
    pub hashed_files: u64,
    pub skipped_files: u64,
    pub errors: Vec<String>,
    pub total_reclaimable_bytes: u64,
}

// ============================================================================
// Git Types
// ============================================================================

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct GitStatus {
    pub is_repo: bool,
    pub branch: Option<String>,
    pub modified: u32,
    pub staged: u32,
    pub untracked: u32,
    pub ahead: u32,
    pub behind: u32,
}

// ============================================================================
// App / Installer Types
// ============================================================================

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct RarInstallPlan {
    pub confirmation_token: String,
    pub download_url: String,
    pub file_name: String,
    pub installer_path: String,
    pub publisher: String,
    pub sha256: String,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct AppAboutInfo {
    pub name: String,
    pub version: String,
    pub identifier: String,
    pub os: String,
    pub arch: String,
    pub authors: String,
    pub repository: String,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct UpdateInfo {
    pub version: String,
    pub date: Option<String>,
    pub body: Option<String>,
    pub installable: bool,
    pub channel: Option<String>,
    pub url: Option<String>,
    pub sha256: Option<String>,
    pub size: Option<u64>,
}

// ============================================================================
// Preview / Thumbnail Types
// ============================================================================

#[derive(Debug, Serialize, Deserialize)]
pub struct FilePreview {
    pub file_type: String,
    pub content: Option<String>,
    pub mime_type: String,
    pub size: u64,
    pub encoding: Option<String>,
}

#[derive(Debug, Serialize)]
pub struct ThumbnailResult {
    pub path: String,
    pub data: Option<String>,
    pub error: Option<String>,
}

// ============================================================================
// Metadata Types
// ============================================================================

/// Detailed metadata for an image file. In addition to the pixel
/// dimensions (`width` and `height`), this struct exposes a list of
/// EXIF fields found in the file. Each entry in the `exif` vector is a
/// `(tag, value)` tuple where `tag` is the human‑readable field name
/// (e.g. "ISO Speed Ratings") and `value` is its value. If no EXIF
/// metadata is present or the file could not be parsed, the `exif`
/// vector will be empty.
#[derive(Debug, Serialize)]
pub struct ImageMetadata {
    pub width: u32,
    pub height: u32,
    pub exif: Vec<(String, String)>,
}

/// Unified file metadata for the properties panel. `kind` is one of
/// `image`, `pdf`, `audio`, `video`, `office`, or `unsupported`.
/// `summary` is a short one-line description suitable for headers.
/// `fields` is an ordered list of `(label, value)` pairs for display.
#[derive(Debug, Serialize, Clone)]
pub struct FileMetadata {
    pub kind: String,
    pub summary: Option<String>,
    pub fields: Vec<(String, String)>,
}

// ============================================================================

#[derive(Debug, Serialize)]
pub struct ArchiveEntry {
    pub name: String,
    pub path: String,
    pub is_dir: bool,
    pub size: u64,
    pub compressed_size: u64,
}

#[derive(Debug, Serialize)]
pub struct ArchiveInfo {
    pub path: String,
    pub format: String,
    pub entries: Vec<ArchiveEntry>,
    pub unsafe_entries: Vec<String>,
    pub total_size: u64,
    pub compressed_size: u64,
}
