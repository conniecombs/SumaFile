//! Archive engines served by a plugin's backend process over a named pipe.
//!
//! The plugin creates the pipe server; the service connects as a client and
//! speaks the same length-prefixed JSON-RPC framing as the host connection.
//!
//! - `connection.rs`: one pipe, its I/O thread, and the call loop with
//!   progress, the idle deadline and the cancel watch.
//! - `pool.rs`: the per-plugin pool. Calls take an idle connection or open
//!   one, up to `max_connections`, so a long extract never blocks a listing.
//! - This file: deadlines, registration with its ticket, and
//!   `PipeArchiveEngine`, which forwards every engine operation.
//!
//! Every exchange is bounded: a plugin that stops answering costs a lane
//! worker for at most one deadline, never indefinitely. Read- and
//! transfer-class calls have idle deadlines that each `archive.progress`
//! for the call's operation resets.

mod connection;
mod pool;

pub use pool::PluginConnection;

use serde_json::{json, Value};
use simplefile_core::archive::{
    self, ArchiveEngine, EngineContext, EngineEntry, EngineFormat, EngineLimits, EngineProgress,
    CANCELLED_MESSAGE,
};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::Duration;

pub const PLUGIN_HANDSHAKE_METHOD: &str = "plugin.handshake";
pub const PLUGIN_SDK_VERSION: &str = "1.0.0";
const CANCEL_METHOD: &str = "cancel";
const MAX_PIPE_NAME_LEN: usize = 200;
const PLUGIN_ID_GRAMMAR: &str = "^[a-z0-9][a-z0-9.-]{1,63}$";

/// How long each kind of exchange with a plugin may take, and how many
/// connections a plugin's pool may hold.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PluginTimeouts {
    pub handshake: Duration,
    /// `archive.list`.
    pub list: Duration,
    /// `archive.read_entry`, an idle deadline.
    pub read: Duration,
    /// Every other `archive.*` method, an idle deadline.
    pub transfer: Duration,
    /// A `cancel` request, and how long a cancelled call then waits for its
    /// own reply.
    pub cancel: Duration,
    /// How long a caller waits for a free connection.
    pub lock: Duration,
    pub max_connections: usize,
}

impl Default for PluginTimeouts {
    fn default() -> Self {
        Self {
            handshake: Duration::from_secs(5),
            list: Duration::from_secs(30),
            read: Duration::from_secs(120),
            transfer: Duration::from_secs(600),
            cancel: Duration::from_secs(5),
            lock: Duration::from_secs(30),
            max_connections: 8,
        }
    }
}

/// A fixed deadline runs from the request; an idle one restarts with each
/// `archive.progress` for the call's operation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DeadlineClass {
    Fixed(Duration),
    Idle(Duration),
}

impl DeadlineClass {
    fn duration(self) -> Duration {
        match self {
            Self::Fixed(duration) | Self::Idle(duration) => duration,
        }
    }
}

impl PluginTimeouts {
    fn for_method(&self, method: &str) -> DeadlineClass {
        match method {
            PLUGIN_HANDSHAKE_METHOD => DeadlineClass::Fixed(self.handshake),
            "archive.list" => DeadlineClass::Fixed(self.list),
            CANCEL_METHOD => DeadlineClass::Fixed(self.cancel),
            "archive.read_entry" => DeadlineClass::Idle(self.read),
            _ => DeadlineClass::Idle(self.transfer),
        }
    }
}

/// Plugin pipe names are bare names: `[A-Za-z0-9._-]{1,200}`. Anything
/// else (separators, a `\\.\pipe\` prefix, a drive path) is refused so the
/// service never opens a path a plugin chose.
pub fn is_valid_pipe_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= MAX_PIPE_NAME_LEN
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

/// The manifest's plugin id grammar: `^[a-z0-9][a-z0-9.-]{1,63}$`.
pub fn is_valid_plugin_id(id: &str) -> bool {
    let bytes = id.as_bytes();
    (2..=64).contains(&bytes.len())
        && matches!(bytes[0], b'a'..=b'z' | b'0'..=b'9')
        && bytes[1..]
            .iter()
            .all(|byte| matches!(byte, b'a'..=b'z' | b'0'..=b'9' | b'.' | b'-'))
}

/// One format of a registration request, as the host sends it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginArchiveFormat {
    pub extension: String,
    pub can_list: bool,
    pub can_extract: bool,
    pub can_create: bool,
    pub can_modify: bool,
    pub engine_name: String,
}

/// `svc-<pid>-<n>`: the operation id of a read or transfer call whose
/// caller has none, so its progress still resets the idle deadline.
fn service_operation_id() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    format!(
        "svc-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed) + 1
    )
}

/// An archive engine that forwards every operation to a plugin backend.
pub struct PipeArchiveEngine {
    name: String,
    connection: Arc<PluginConnection>,
}

impl PipeArchiveEngine {
    pub fn new(name: impl Into<String>, connection: Arc<PluginConnection>) -> Self {
        Self {
            name: name.into(),
            connection,
        }
    }
}

impl ArchiveEngine for PipeArchiveEngine {
    fn name(&self) -> &str {
        &self.name
    }

    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String> {
        let result = self.connection.call(
            "archive.list",
            json!({ "archivePath": archive.to_string_lossy() }),
        )?;
        let entries = result
            .get("entries")
            .and_then(Value::as_array)
            .ok_or_else(|| format!("{} returned no entries array", self.name))?;
        entries
            .iter()
            .map(|entry| {
                let path = entry
                    .get("path")
                    .and_then(Value::as_str)
                    .ok_or_else(|| format!("{} returned an entry without a path", self.name))?;
                Ok(EngineEntry {
                    path: path.to_string(),
                    is_dir: entry.get("isDir").and_then(Value::as_bool).unwrap_or(false),
                    size: entry.get("size").and_then(Value::as_u64).unwrap_or(0),
                    compressed_size: entry
                        .get("compressedSize")
                        .and_then(Value::as_u64)
                        .unwrap_or(0),
                })
            })
            .collect()
    }

    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.call_tracked(
            "archive.extract",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": destination.to_string_lossy(),
            }),
            ctx,
        )
    }

    fn read_entry(
        &self,
        archive: &Path,
        entry: &str,
        destination: &Path,
        limits: EngineLimits,
    ) -> Result<(), String> {
        // No context reaches a read, so it carries a service id: its
        // progress still extends the read deadline.
        self.connection
            .call(
                "archive.read_entry",
                json!({
                    "archivePath": archive.to_string_lossy(),
                    "innerPath": entry,
                    "destination": destination.to_string_lossy(),
                    "maxBytes": limits.max_bytes,
                    "maxEntries": limits.max_entries,
                    "operationId": service_operation_id(),
                }),
            )
            .map(|_| ())
    }

    fn create(
        &self,
        archive: &Path,
        paths: &[PathBuf],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        let paths: Vec<String> = paths
            .iter()
            .map(|path| path.to_string_lossy().into_owned())
            .collect();
        self.call_tracked(
            "archive.create",
            json!({ "archivePath": archive.to_string_lossy(), "paths": paths }),
            ctx,
        )
    }

    fn rename(
        &self,
        archive: &Path,
        entry: &str,
        new_path: &str,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.call_tracked(
            "archive.rename",
            json!({
                "archivePath": archive.to_string_lossy(),
                "innerPath": entry,
                "newPath": new_path,
            }),
            ctx,
        )
    }

    fn delete(
        &self,
        archive: &Path,
        entries: &[String],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.call_tracked(
            "archive.delete",
            json!({ "archivePath": archive.to_string_lossy(), "innerPaths": entries }),
            ctx,
        )
    }

    fn add(
        &self,
        archive: &Path,
        base_dir: &Path,
        paths: &[String],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.call_tracked(
            "archive.add",
            json!({
                "archivePath": archive.to_string_lossy(),
                "baseDir": base_dir.to_string_lossy(),
                "paths": paths,
            }),
            ctx,
        )
    }
}

impl PipeArchiveEngine {
    /// Send a call that belongs to an operation. It is refused once the
    /// caller has cancelled. It carries `ctx.operation_id`, or a service id
    /// when the context has none. The plugin's progress is relayed to
    /// `ctx`, and a cancel set while it runs is forwarded to the plugin.
    /// Every such method replies `null`.
    fn call_tracked(
        &self,
        method: &str,
        params: Value,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        if ctx.is_cancelled() {
            return Err(CANCELLED_MESSAGE.to_string());
        }
        let operation_id = ctx
            .operation_id
            .map(str::to_string)
            .unwrap_or_else(service_operation_id);
        let progress = |update: EngineProgress| {
            ctx.report(update.current, update.total, update.current_item.as_deref());
        };
        self.connection
            .call_tracked(method, params, &operation_id, ctx.cancel, &progress)
            .map(|_| ())
    }
}

/// The latest registration change the service received for each plugin.
static REGISTRATION_SEQUENCES: LazyLock<Mutex<HashMap<String, u64>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

fn registration_sequences() -> std::sync::MutexGuard<'static, HashMap<String, u64>> {
    REGISTRATION_SEQUENCES
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// A place in a plugin's sequence of registration changes. Only the change
/// holding the latest ticket may commit, so the last `register` or
/// `unregister` the service received wins, whatever order the work
/// finishes in.
#[derive(Debug)]
pub struct RegistrationTicket {
    plugin_id: String,
    sequence: u64,
}

impl RegistrationTicket {
    pub fn plugin_id(&self) -> &str {
        &self.plugin_id
    }
}

/// Advance `plugin_id`'s sequence and return the new latest ticket. The
/// `register_archive_engine` handler takes it on the session loop, before
/// it defers the connect, so tickets follow the order the host sent.
pub fn registration_ticket(plugin_id: &str) -> RegistrationTicket {
    let mut sequences = registration_sequences();
    let sequence = sequences.entry(plugin_id.to_string()).or_insert(0);
    *sequence += 1;
    RegistrationTicket {
        plugin_id: plugin_id.to_string(),
        sequence: *sequence,
    }
}

/// Connect to `pipe_name`, handshake, then replace whatever the ticket's
/// plugin registered before with `formats`. Formats that share an engine
/// name share one engine; all share one connection pool. Formats no engine
/// may serve (empty or built-in extensions) are dropped first; if none
/// remain, or on any failure, the existing registration is left untouched.
/// The change commits only if the ticket is still the plugin's latest.
pub fn register_plugin_engine(
    ticket: RegistrationTicket,
    pipe_name: &str,
    formats: Vec<PluginArchiveFormat>,
) -> Result<(), String> {
    let plugin_id = ticket.plugin_id.as_str();
    if !is_valid_plugin_id(plugin_id) {
        return Err(format!("pluginId must match {PLUGIN_ID_GRAMMAR}"));
    }
    if !is_valid_pipe_name(pipe_name) {
        return Err(format!("Invalid plugin pipe name: {pipe_name}"));
    }
    let formats: Vec<PluginArchiveFormat> = formats
        .into_iter()
        .filter(|format| archive::is_registrable_extension(&format.extension))
        .collect();
    if formats.is_empty() {
        return Err(format!(
            "Plugin {plugin_id} has no registrable formats (empty or built-in extensions only)"
        ));
    }

    let connection = PluginConnection::connect(plugin_id, pipe_name)?;

    let mut groups: Vec<(String, Vec<EngineFormat>)> = Vec::new();
    for format in formats {
        let engine_format = EngineFormat {
            extension: format.extension,
            can_list: format.can_list,
            can_extract: format.can_extract,
            can_create: format.can_create,
            can_modify: format.can_modify,
        };
        let engine_name = if format.engine_name.trim().is_empty() {
            plugin_id.to_string()
        } else {
            format.engine_name
        };
        match groups.iter_mut().find(|(name, _)| *name == engine_name) {
            Some((_, group)) => group.push(engine_format),
            None => groups.push((engine_name, vec![engine_format])),
        }
    }

    let engines: Vec<(Vec<EngineFormat>, Arc<dyn ArchiveEngine>)> = groups
        .into_iter()
        .map(|(engine_name, group)| {
            let engine: Arc<dyn ArchiveEngine> =
                Arc::new(PipeArchiveEngine::new(engine_name, Arc::clone(&connection)));
            (group, engine)
        })
        .collect();

    let sequences = registration_sequences();
    if sequences.get(plugin_id) != Some(&ticket.sequence) {
        return Err(format!(
            "Registration of plugin {plugin_id} was superseded by a later registration change"
        ));
    }
    if !archive::replace_engines(plugin_id, engines) {
        return Err(format!("Plugin {plugin_id} has no registrable formats"));
    }
    drop(sequences);
    Ok(())
}

/// Drop every engine `plugin_id` registered. It advances the plugin's
/// sequence first, under the same lock, so a register still connecting can
/// never land after it. Unknown ids are a no-op.
pub fn unregister_plugin_engine(plugin_id: &str) {
    let mut sequences = registration_sequences();
    *sequences.entry(plugin_id.to_string()).or_insert(0) += 1;
    archive::unregister_engine(plugin_id);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timeouts_by_method_follow_the_phase_2_table() {
        let timeouts = PluginTimeouts::default();
        assert_eq!(
            timeouts,
            PluginTimeouts {
                handshake: Duration::from_secs(5),
                list: Duration::from_secs(30),
                read: Duration::from_secs(120),
                transfer: Duration::from_secs(600),
                cancel: Duration::from_secs(5),
                lock: Duration::from_secs(30),
                max_connections: 8,
            }
        );
        assert_eq!(
            timeouts.for_method("plugin.handshake"),
            DeadlineClass::Fixed(timeouts.handshake)
        );
        assert_eq!(
            timeouts.for_method("archive.list"),
            DeadlineClass::Fixed(timeouts.list)
        );
        assert_eq!(
            timeouts.for_method("cancel"),
            DeadlineClass::Fixed(timeouts.cancel)
        );
        assert_eq!(
            timeouts.for_method("archive.read_entry"),
            DeadlineClass::Idle(timeouts.read)
        );
        for method in [
            "archive.extract",
            "archive.create",
            "archive.rename",
            "archive.delete",
            "archive.add",
        ] {
            assert_eq!(
                timeouts.for_method(method),
                DeadlineClass::Idle(timeouts.transfer),
                "{method}"
            );
        }
    }

    #[test]
    fn plugin_ids_follow_the_manifest_grammar() {
        for valid in ["com.sumafile.sevenzip", "a1", "0-x.y", &"a".repeat(64)] {
            assert!(is_valid_plugin_id(valid), "{valid}");
        }
        for invalid in [
            "",
            "a",
            "Bad.Id",
            "x/y",
            ".abc",
            "-abc",
            &"a".repeat(65),
            "a b",
        ] {
            assert!(!is_valid_plugin_id(invalid), "{invalid}");
        }
    }

    #[test]
    fn service_operation_ids_are_unique_and_prefixed() {
        let first = service_operation_id();
        let second = service_operation_id();
        assert!(first.starts_with(&format!("svc-{}-", std::process::id())));
        assert_ne!(first, second);
    }
}
