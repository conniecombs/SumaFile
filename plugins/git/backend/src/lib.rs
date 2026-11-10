//! Backend of the first-party SumaFile Git plugin (`com.sumafile.git`).
//!
//! It ports 1.0.4's `simplefile-core/src/git.rs` and its status cache into a
//! process of its own: repository status, per-folder row decorations, the
//! file-level commands (stage, unstage, discard, diff) and commit, fetch,
//! pull and push. Every git process runs in its own kill-on-close job (see
//! [`run`]). Read-only queries (status, rev-parse, decorations, the diff
//! preview) run no program the repository's config names: no hook, no
//! fsmonitor, no filter driver, no external diff or textconv and no git inside
//! a submodule (see [`git`]); the path commands and commit keep hooks and
//! filters on. The status cache is per repository root and shares a run in
//! flight (see [`status`]).
//! The handler serves the methods of `plugins/git/protocol.json`.

pub mod commands;
pub mod decorations;
pub mod git;
pub mod handler;
pub mod models;
pub mod paths;
pub mod run;
pub mod status;

/// The plugin id, as in `plugins/git/plugin.json`.
pub const PLUGIN_ID: &str = "com.sumafile.git";

/// The plugin version reported in the handshake, as in `plugin.json`.
pub const PLUGIN_VERSION: &str = "1.0.0";

/// Every call fails with this, and spawns nothing, when no absolute `PATH`
/// entry holds git.
pub const GIT_NOT_FOUND: &str =
    "Git was not found. Install Git for Windows or add git.exe to PATH.";

/// A git command stopped by `cancel` fails with this.
pub const GIT_CANCELLED: &str = "Git operation cancelled";
