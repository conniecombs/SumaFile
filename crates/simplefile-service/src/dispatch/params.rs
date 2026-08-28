use serde::Deserialize;
use serde_json::Value;
use simplefile_core::dir_list::{ListDirectoryOptions, ListingMode};
use simplefile_core::models::SearchOptions;
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

#[derive(Debug, Deserialize)]
pub(super) struct NameParams {
    pub(super) path: String,
    pub(super) name: String,
}

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
    pub sources: Vec<String>,
    pub destination: String,
    #[serde(rename = "operationId")]
    pub operation_id: Option<String>,
    #[serde(rename = "conflictAction")]
    pub conflict_action: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct OperationIdParams {
    #[serde(rename = "operationId")]
    pub(super) operation_id: String,
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
pub(super) struct ThumbnailParams {
    pub(super) path: String,
    pub(super) size: Option<u32>,
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
}

#[derive(Debug, Deserialize)]
pub(super) struct CreateArchiveParams {
    pub(super) paths: Vec<String>,
    #[serde(rename = "archivePath")]
    pub(super) archive_path: String,
    pub(super) format: String,
}

#[derive(Debug, Deserialize)]
pub(super) struct DuplicateCheckParams {
    pub(super) directory: String,
    #[serde(rename = "minSize")]
    pub(super) min_size: Option<u64>,
    #[serde(rename = "partialHashBytes")]
    pub(super) partial_hash_bytes: Option<u64>,
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
pub(super) struct ConfirmationTokenParams {
    #[serde(rename = "confirmationToken")]
    pub(super) confirmation_token: String,
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

pub(super) fn parse_path_params(request: &JsonRpcRequest) -> Result<String, JsonRpcResponse> {
    parse_params::<PathParams>(request).map(|params| params.path)
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
