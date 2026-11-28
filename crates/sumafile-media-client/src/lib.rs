//! The file service's side of the media process (`sumafile-media`): the wire
//! format both ends speak, and the client that starts the process on first
//! use, bounds every request with a timeout and keeps one bad file from
//! costing more than itself.

mod client;
mod process;
pub mod protocol;

pub use client::{
    default_helper_path, ClientConfig, ClientStats, MediaClient, MediaError, Rendered,
    HELPER_FILE_NAME, HELPER_PATH_VARIABLE,
};
