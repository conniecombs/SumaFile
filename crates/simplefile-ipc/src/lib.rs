//! IPC protocol constants for the WinUI ↔ Rust named-pipe JSON-RPC host.
//!
//! Schemas live in `ipc/schema/v1/` until a C# project exists. This crate
//! does not implement the pipe server.

pub const PROTOCOL_VERSION: u32 = 1;
pub const JSONRPC_VERSION: &str = "2.0";
pub const MAX_FRAME_BYTES: u32 = 80 * 1024 * 1024;
pub const DOMAIN_METHOD_COUNT: usize = 76;

pub const BINARY_FRAME_MAGIC: [u8; 4] = *b"SFB1";
pub const BINARY_FRAME_VERSION: u8 = 1;
pub const BINARY_LIST_DIRECTORY_CHUNK: u8 = 1;
pub const BINARY_LIST_DIRECTORY_RESULT: u8 = 2;
pub const BINARY_SEARCH_RESULTS_BATCH: u8 = 3;
pub const BINARY_SEARCH_RESULTS_RESULT: u8 = 4;
pub const BINARY_OPERATION_PROGRESS: u8 = 5;
pub const BINARY_FILE_CHANGE: u8 = 6;
pub const BINARY_THUMBNAIL_RESULT: u8 = 7;
pub const BINARY_THUMBNAILS_RESULT: u8 = 8;

pub const ERR_APPLICATION: i32 = -32000;
pub const ERR_HOST_OWNED: i32 = -32001;
pub const ERR_INVALID_REQUEST: i32 = -32600;
pub const ERR_METHOD_NOT_FOUND: i32 = -32601;
pub const ERR_INVALID_PARAMS: i32 = -32602;
pub const ERR_INTERNAL: i32 = -32603;

pub const HANDSHAKE_METHOD: &str = "ipc.handshake";
pub const LIST_DIRECTORY_CHUNK: &str = "list_directory.chunk";
pub const OPERATION_PROGRESS: &str = "operation-progress";
pub const FILE_CHANGE: &str = "file-change";
pub const SEARCH_RESULTS_BATCH: &str = "search-results-batch";
pub const SEARCH_COMPLETE: &str = "search-complete";
pub const UPDATE_CHUNK: &str = "update-chunk";

pub const PREFIX_CONFLICT: &str = "CONFLICT:";
pub const PREFIX_TRASH_UNAVAILABLE: &str = "TRASH_UNAVAILABLE:";
pub const PREFIX_RESULT_TOO_LARGE: &str = "RESULT_TOO_LARGE:";
pub const PREFIX_HOST_OWNED: &str = "HOST_OWNED:";

pub const HEALTH_METHOD: &str = "ipc.health";
pub const SHUTDOWN_METHOD: &str = "ipc.shutdown";
pub const APP_IDENTIFIER: &str = "com.simplefile.desktop";

pub mod frame;
pub mod rpc;
