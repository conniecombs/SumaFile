//! `GitHandler`: the ten plugin-owned methods of `com.sumafile.git` (see
//! `plugins/git/protocol.json`), served over the plugin pipe.
//!
//! Every git call runs in `spawn_blocking`, so the connection keeps reading
//! cancels and other calls while one runs. fetch, pull and push show a
//! Transfers row; a `cancel` naming the request's `operationId` or the row's
//! id stops them, and so does one for a commit's `operationId` or a
//! decorations request's `operationId`.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use serde::de::DeserializeOwned;
use serde::Deserialize;
use serde_json::{json, Value};
use sumafile_plugin::{async_trait, HostClient, PluginError, PluginHandler};

use crate::commands::{
    commit, diff, discard, remote_in_root, repository_root, stage, unstage, RemoteCommand,
};
use crate::decorations::folder_decorations_with_cancel;
use crate::git::Git;
use crate::run::CancelCheck;
use crate::status::{repo_status, StatusCache, STATUS_CACHE_CAPACITY, STATUS_CACHE_TTL};

const REPO_STATUS: &str = "repo.status";
const REPO_DECORATIONS: &str = "repo.decorations";
const PATHS_STAGE: &str = "paths.stage";
const PATHS_UNSTAGE: &str = "paths.unstage";
const PATHS_DISCARD: &str = "paths.discard";
const PATHS_DIFF: &str = "paths.diff";
const REPO_COMMIT: &str = "repo.commit";
const REMOTE_FETCH: &str = "remote.fetch";
const REMOTE_PULL: &str = "remote.pull";
const REMOTE_PUSH: &str = "remote.push";

#[derive(Deserialize)]
struct PathParams {
    path: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct FolderParams {
    folder: String,
    #[serde(default)]
    operation_id: Option<String>,
}

#[derive(Deserialize)]
struct PathsParams {
    path: String,
    paths: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DiffParams {
    path: String,
    file_path: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CommitParams {
    path: String,
    message: String,
    #[serde(default)]
    operation_id: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RemoteParams {
    path: String,
    #[serde(default)]
    operation_id: Option<String>,
}

fn parse<T: DeserializeOwned>(method: &str, params: Value) -> Result<T, PluginError> {
    let params = if params.is_null() { json!({}) } else { params };
    serde_json::from_value(params)
        .map_err(|error| PluginError::InvalidParams(format!("Invalid {method} params: {error}")))
}

fn to_value(value: impl serde::Serialize) -> Result<Value, PluginError> {
    serde_json::to_value(value).map_err(|error| PluginError::Application(error.to_string()))
}

/// Runs a blocking call on the blocking pool; its error is the reply.
async fn blocking<T, F>(call: F) -> Result<T, PluginError>
where
    T: Send + 'static,
    F: FnOnce() -> Result<T, String> + Send + 'static,
{
    tokio::task::spawn_blocking(call)
        .await
        .map_err(|error| PluginError::Application(format!("git call failed: {error}")))?
        .map_err(PluginError::Application)
}

/// True once a `cancel` named any of `ids`.
fn cancel_check(host: &HostClient, ids: Vec<String>) -> CancelCheck {
    let host = host.clone();
    Arc::new(move || ids.iter().any(|id| host.is_cancelled(id)))
}

/// The repository's folder name for the Transfers row's title.
fn folder_name(root: &Path) -> String {
    root.file_name()
        .map(|name| name.to_string_lossy().to_string())
        .unwrap_or_else(|| root.to_string_lossy().to_string())
}

pub struct GitHandler {
    git: Arc<Git>,
    cache: Arc<StatusCache>,
}

impl GitHandler {
    /// The production handler: git from `PATH`, hooks off inside `data_dir`.
    pub fn new(data_dir: PathBuf) -> Self {
        GitHandler::with_git(Git::discover(&data_dir))
    }

    pub fn with_git(git: Git) -> Self {
        GitHandler {
            git: Arc::new(git),
            cache: Arc::new(StatusCache::new(STATUS_CACHE_TTL, STATUS_CACHE_CAPACITY)),
        }
    }

    async fn paths_command(
        &self,
        method: &str,
        params: Value,
        run: fn(
            &Git,
            &StatusCache,
            &str,
            &[String],
        ) -> Result<crate::models::GitCommandResult, String>,
    ) -> Result<Value, PluginError> {
        let params: PathsParams = parse(method, params)?;
        let (git, cache) = (self.git.clone(), self.cache.clone());
        to_value(blocking(move || run(&git, &cache, &params.path, &params.paths)).await?)
    }

    /// fetch, pull or push with a Transfers row titled `<verb> <folder>`.
    /// Without a host connection it runs without a row.
    async fn remote(
        &self,
        method: &str,
        command: RemoteCommand,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError> {
        let params: RemoteParams = parse(method, params)?;
        let git = self.git.clone();
        let path = params.path.clone();
        let root = blocking(move || repository_root(&git, &path)).await?;

        let title = format!("{} {}", command.progress_verb(), folder_name(&root));
        let row_id = match host.progress_begin(&title).await {
            Ok(row_id) => Some(row_id),
            Err(error) => {
                let _ = host
                    .log(
                        "warn",
                        &format!(
                            "The Transfers row for git {} could not be opened; it runs without one: {error}",
                            command.subcommand()
                        ),
                    )
                    .await;
                None
            }
        };
        let ids = params
            .operation_id
            .iter()
            .chain(row_id.iter())
            .cloned()
            .collect();
        let cancel = cancel_check(host, ids);
        let (git, cache) = (self.git.clone(), self.cache.clone());
        let result =
            blocking(move || remote_in_root(&git, &cache, &root, command, Some(cancel))).await;

        if let Some(row_id) = &row_id {
            let _ = match &result {
                Ok(_) => host.progress_complete(row_id).await,
                Err(error) => host.progress_fail(row_id, error.message()).await,
            };
        }
        to_value(result?)
    }
}

#[async_trait]
impl PluginHandler for GitHandler {
    async fn handle(
        &self,
        method: &str,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError> {
        match method {
            REPO_STATUS => {
                let params: PathParams = parse(method, params)?;
                let (git, cache) = (self.git.clone(), self.cache.clone());
                to_value(
                    blocking(move || repo_status(&git, &cache, Path::new(&params.path))).await?,
                )
            }
            REPO_DECORATIONS => {
                let params: FolderParams = parse(method, params)?;
                let cancel = params.operation_id.map(|id| cancel_check(host, vec![id]));
                let (git, cache) = (self.git.clone(), self.cache.clone());
                to_value(
                    blocking(move || {
                        folder_decorations_with_cancel(
                            &git,
                            &cache,
                            Path::new(&params.folder),
                            cancel.as_ref(),
                        )
                    })
                    .await?,
                )
            }
            PATHS_STAGE => self.paths_command(method, params, stage).await,
            PATHS_UNSTAGE => self.paths_command(method, params, unstage).await,
            PATHS_DISCARD => self.paths_command(method, params, discard).await,
            PATHS_DIFF => {
                let params: DiffParams = parse(method, params)?;
                let (git, cache) = (self.git.clone(), self.cache.clone());
                to_value(
                    blocking(move || diff(&git, &cache, &params.path, &params.file_path)).await?,
                )
            }
            REPO_COMMIT => {
                let params: CommitParams = parse(method, params)?;
                let cancel = cancel_check(host, params.operation_id.into_iter().collect());
                let (git, cache) = (self.git.clone(), self.cache.clone());
                to_value(
                    blocking(move || {
                        commit(&git, &cache, &params.path, &params.message, Some(cancel))
                    })
                    .await?,
                )
            }
            REMOTE_FETCH => {
                self.remote(method, RemoteCommand::Fetch, params, host)
                    .await
            }
            REMOTE_PULL => self.remote(method, RemoteCommand::Pull, params, host).await,
            REMOTE_PUSH => self.remote(method, RemoteCommand::Push, params, host).await,
            _ => Err(PluginError::MethodNotFound),
        }
    }
}
