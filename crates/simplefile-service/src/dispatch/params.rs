use serde::Deserialize;
use serde_json::Value;
use simplefile_core::dir_list::{ListDirectoryOptions, ListingMode};
use simplefile_core::listing_view::ListingViewOptions;
use simplefile_core::models::{EntryFact, SearchOptions};
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use simplefile_ipc::ERR_INVALID_PARAMS;

#[derive(Debug, Deserialize)]
pub(super) struct HandshakeParams {
    #[serde(rename = "protocolVersion")]
    pub(super) protocol_version: u32,
    #[serde(rename = "clientName")]
    #[allow(dead_code)]
    pub(super) client_name: Option<String>,
    #[serde(rename = "authToken")]
    pub(super) auth_token: Option<String>,
    #[serde(rename = "binaryHotFrames", default)]
    pub(super) binary_hot_frames: bool,
}

#[derive(Debug, Deserialize)]
pub(super) struct PathParams {
    pub(super) path: String,
}

/// Which folder's size or count request to cancel. Without a path, every
/// running request of that kind is cancelled.
#[derive(Debug, Default, Deserialize)]
pub(super) struct CancelFolderParams {
    #[serde(default)]
    pub(super) path: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
pub(super) struct DriveListParams {
    #[serde(default)]
    pub(super) mode: Option<String>,
    #[serde(default)]
    pub(super) path: Option<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct ListDirectoryParams {
    pub(super) path: String,
    #[serde(default)]
    pub(super) mode: Option<String>,
    #[serde(rename = "finalEntries", default)]
    pub(super) final_entries: Option<bool>,
    #[serde(rename = "sortBy", default)]
    pub(super) sort_by: Option<String>,
    #[serde(rename = "sortAscending", default)]
    pub(super) sort_ascending: Option<bool>,
    #[serde(default)]
    pub(super) filter: Option<String>,
    #[serde(rename = "includeHidden", default)]
    pub(super) include_hidden: Option<bool>,
}

impl ListDirectoryParams {
    pub(super) fn into_options(self) -> (String, Option<ListDirectoryOptions>) {
        let has_options = self.mode.is_some()
            || self.final_entries.is_some()
            || self.sort_by.is_some()
            || self.sort_ascending.is_some()
            || self.filter.is_some()
            || self.include_hidden.is_some();
        if !has_options {
            return (self.path, None);
        }

        let mode = match self.mode.as_deref() {
            Some("light") => ListingMode::Light,
            _ => ListingMode::Full,
        };
        (
            self.path,
            Some(ListDirectoryOptions {
                mode,
                final_entries: self.final_entries.unwrap_or(true),
                sort_by: self.sort_by.unwrap_or_else(|| "name".to_string()),
                sort_ascending: self.sort_ascending.unwrap_or(true),
                filter: self.filter,
                include_hidden: self.include_hidden.unwrap_or(true),
            }),
        )
    }
}

/// `open_listing_view`: the folder a pane shows and the order and filter it
/// wants. Every option is optional; an omitted one is the view's default.
#[derive(Debug, Deserialize)]
pub(crate) struct OpenListingViewParams {
    #[serde(rename = "viewId")]
    pub(crate) view_id: String,
    pub(crate) path: String,
    #[serde(rename = "sortBy", default)]
    pub(crate) sort_by: Option<String>,
    #[serde(rename = "sortAscending", default)]
    pub(crate) sort_ascending: Option<bool>,
    #[serde(rename = "foldersFirst", default)]
    pub(crate) folders_first: Option<bool>,
    #[serde(rename = "includeHidden", default)]
    pub(crate) include_hidden: Option<bool>,
    #[serde(default)]
    pub(crate) filter: Option<String>,
    #[serde(default)]
    pub(crate) locale: Option<String>,
    #[serde(rename = "pageSize", default)]
    pub(crate) page_size: Option<u32>,
    /// Enumerate again even when the view already shows the folder.
    #[serde(default)]
    pub(crate) reload: Option<bool>,
    /// Only re-sort and re-filter the view the pane already has of this
    /// folder: never enumerate, and fail when the view shows another folder.
    #[serde(default)]
    pub(crate) reuse: Option<bool>,
}

impl OpenListingViewParams {
    pub(crate) fn options(&self) -> ListingViewOptions {
        let defaults = ListingViewOptions::default();
        ListingViewOptions {
            sort_by: self.sort_by.clone().unwrap_or(defaults.sort_by),
            sort_ascending: self.sort_ascending.unwrap_or(defaults.sort_ascending),
            folders_first: self.folders_first.unwrap_or(defaults.folders_first),
            include_hidden: self.include_hidden.unwrap_or(defaults.include_hidden),
            filter: self.filter.clone(),
            locale: self.locale.clone(),
        }
    }
}

#[derive(Debug, Deserialize)]
pub(super) struct ListingViewIdParams {
    #[serde(rename = "viewId")]
    pub(super) view_id: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct ListingViewRangeParams {
    #[serde(rename = "viewId")]
    pub(super) view_id: String,
    #[serde(default)]
    pub(super) offset: u32,
    #[serde(default)]
    pub(super) count: u32,
}

#[derive(Debug, Deserialize)]
pub(super) struct NameParams {
    pub(super) path: String,
    pub(super) name: String,
}

pub(super) type ShortcutParams = simplefile_core::file_ops::ShortcutRequest;

#[derive(Debug, Deserialize)]
pub(super) struct PathsParams {
    pub(super) paths: Vec<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct RenameParams {
    pub(super) path: String,
    #[serde(rename = "newName")]
    pub(super) new_name: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct BatchRenameParams {
    pub(super) entries: Vec<simplefile_core::file_ops::RenameRequest>,
}

#[derive(Debug, Deserialize)]
pub(super) struct CopyMoveParams {
    pub(super) source: String,
    pub(super) destination: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct ResolvedCopyMoveParams {
    pub(super) source: String,
    pub(super) destination: String,
    #[serde(rename = "conflictAction")]
    pub(super) conflict_action: String,
}

#[derive(Debug, Deserialize)]
pub(crate) struct ProgressCopyMoveParams {
    #[serde(default)]
    pub sources: Vec<String>,
    pub destination: String,
    #[serde(rename = "operationId")]
    pub operation_id: Option<String>,
    #[serde(rename = "conflictAction", default = "default_conflict_action")]
    pub conflict_action: String,
    /// Targeted retry: exact files to transfer again with their own actions.
    /// When present, `sources` and `conflictAction` are not used for planning.
    #[serde(default)]
    pub items: Option<Vec<ProgressTransferItem>>,
}

fn default_conflict_action() -> String {
    "error".to_string()
}

#[derive(Debug, Clone, Deserialize)]
pub(crate) struct ProgressTransferItem {
    pub source: String,
    pub destination: String,
    #[serde(default = "default_conflict_action")]
    pub action: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct OperationIdParams {
    #[serde(rename = "operationId")]
    pub(super) operation_id: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct OptionalOperationIdParams {
    #[serde(rename = "operationId")]
    pub(super) operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct SearchFilesParams {
    pub(super) options: SearchOptions,
}

#[derive(Debug, Deserialize)]
pub(super) struct SearchIdParams {
    #[serde(rename = "searchId")]
    pub(super) search_id: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct PreviewParams {
    pub(super) path: String,
    #[serde(rename = "maxSize")]
    pub(super) max_size: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct ThumbnailParams {
    pub(super) path: String,
    pub(super) size: Option<u32>,
    /// For a video: how far into it the frame is, in percent.
    #[serde(default)]
    pub(super) position_percent: Option<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct ImagePreviewParams {
    pub(super) path: String,
    pub(super) max_dimension: Option<u32>,
}

#[derive(Debug, Deserialize)]
pub(super) struct ThumbnailBatchParams {
    pub(super) paths: Vec<String>,
    pub(super) size: Option<u32>,
}

#[derive(Debug, Deserialize)]
pub(super) struct ExternalUrlParams {
    pub(super) url: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct SettingKeyParams {
    pub(super) key: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct SettingKeysParams {
    pub(super) keys: Vec<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct SettingValueParams {
    pub(super) key: String,
    pub(super) value: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct OpenWithParams {
    pub(super) path: String,
    pub(super) application: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct CompareParams {
    #[serde(rename = "pathA")]
    pub(super) path_a: String,
    #[serde(rename = "pathB")]
    pub(super) path_b: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct ExtractArchiveParams {
    #[serde(rename = "archivePath")]
    pub(super) archive_path: String,
    pub(super) destination: String,
    /// Present for a tracked extract: progress, cancel and a Transfers row.
    #[serde(rename = "operationId", default)]
    pub(super) operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct ArchiveEngineFormat {
    pub(super) extension: String,
    pub(super) can_list: bool,
    pub(super) can_extract: bool,
    pub(super) can_create: bool,
    pub(super) can_modify: bool,
    pub(super) engine_name: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct RegisterArchiveEngineParams {
    pub(super) plugin_id: String,
    pub(super) pipe_name: String,
    /// The token the app launched the plugin's backend with, which the
    /// backend expects in every handshake. An app from before launch tokens
    /// sends none.
    #[serde(default)]
    pub(super) auth_token: Option<String>,
    pub(super) formats: Vec<ArchiveEngineFormat>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct PluginIdParams {
    pub(super) plugin_id: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct CreateArchiveParams {
    pub(super) paths: Vec<String>,
    #[serde(rename = "archivePath")]
    pub(super) archive_path: String,
    pub(super) format: String,
    /// Present for a tracked create: progress, cancel and a Transfers row.
    #[serde(rename = "operationId", default)]
    pub(super) operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct DuplicateCheckParams {
    pub(super) directory: String,
    #[serde(rename = "minSize")]
    pub(super) min_size: Option<u64>,
    #[serde(rename = "partialHashBytes")]
    pub(super) partial_hash_bytes: Option<u64>,
    #[serde(rename = "maxDepth")]
    pub(super) max_depth: Option<usize>,
    #[serde(rename = "excludePatterns")]
    pub(super) exclude_patterns: Option<Vec<String>>,
    #[serde(rename = "networkMode")]
    pub(super) network_mode: Option<bool>,
    #[serde(rename = "operationId")]
    pub(super) operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct DiskCleanupParams {
    pub(super) directory: String,
    #[serde(rename = "sizeThreshold")]
    pub(super) size_threshold: Option<u64>,
    #[serde(rename = "operationId")]
    pub(super) operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
pub(super) struct TagCreateParams {
    pub(super) name: String,
    pub(super) color: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct TagUpdateParams {
    pub(super) id: i64,
    pub(super) name: String,
    pub(super) color: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct TagIdParams {
    pub(super) id: i64,
}

#[derive(Debug, Deserialize)]
pub(super) struct TagForPathParams {
    pub(super) path: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct SetTagsForPathParams {
    pub(super) path: String,
    #[serde(rename = "tagIds")]
    pub(super) tag_ids: Vec<i64>,
}

#[derive(Debug, Deserialize)]
pub(super) struct GetFilesWithTagParams {
    #[serde(rename = "tagId")]
    pub(super) tag_id: i64,
}

#[derive(Debug, Deserialize)]
pub(super) struct SmartFolderParams {
    pub(super) folder: simplefile_core::models::SmartFolder,
}

#[derive(Debug, Deserialize)]
pub(super) struct SmartFolderIdParams {
    pub(super) id: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct WatchDirectoryParams {
    #[serde(default)]
    pub(super) path: Option<String>,
    #[serde(default)]
    pub(super) paths: Option<Vec<String>>,
}

pub(super) fn parse_watch_directory_params(
    request: &JsonRpcRequest,
) -> Result<Vec<String>, JsonRpcResponse> {
    let params = parse_params::<WatchDirectoryParams>(request)?;
    let mut out = Vec::new();
    if let Some(path) = params.path {
        if !path.is_empty() {
            out.push(path);
        }
    }
    if let Some(paths) = params.paths {
        for path in paths {
            if !path.is_empty() && !out.contains(&path) {
                out.push(path);
            }
        }
    }
    if out.is_empty() {
        return Err(JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_PARAMS,
            "invalid params: missing field `path`",
        ));
    }
    Ok(out)
}

pub(super) fn parse_params<T: for<'de> Deserialize<'de>>(
    request: &JsonRpcRequest,
) -> Result<T, JsonRpcResponse> {
    let params = request
        .params
        .clone()
        .unwrap_or(Value::Object(Default::default()));
    serde_json::from_value(params).map_err(|error| {
        JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_PARAMS,
            format!("invalid params: {error}"),
        )
    })
}

pub(super) fn parse_nullable_params<T>(request: &JsonRpcRequest) -> Result<T, JsonRpcResponse>
where
    T: for<'de> Deserialize<'de> + Default,
{
    match request.params.clone() {
        Some(Value::Null) | None => Ok(T::default()),
        Some(params) => serde_json::from_value(params).map_err(|error| {
            JsonRpcResponse::error(
                request.id.clone(),
                ERR_INVALID_PARAMS,
                format!("invalid params: {error}"),
            )
        }),
    }
}

#[derive(Debug, Deserialize)]
pub(crate) struct EntryFactsParams {
    #[serde(rename = "requestId")]
    pub(crate) request_id: String,
    pub(crate) paths: Vec<String>,
    pub(crate) kinds: Vec<String>,
}

#[derive(Debug, Deserialize)]
pub(crate) struct CancelEntryFactsParams {
    #[serde(rename = "requestId")]
    pub(crate) request_id: String,
}

#[derive(Debug, Deserialize)]
pub(crate) struct PutEntryFactsParams {
    pub(crate) facts: Vec<EntryFact>,
}
