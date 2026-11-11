mod capabilities;
mod create;
pub mod engine;
mod extract;
mod list;
mod mutate;
mod mutate_engine;
mod path;
mod placeholders;

#[cfg(test)]
mod tests;

pub use capabilities::get_archive_capabilities;
pub use create::{create_archive, create_archive_with};
pub use engine::{
    engine_for_path, is_registrable_extension, register_engine, registered_formats,
    replace_engines, unregister_engine, ArchiveEngine, EngineContext, EngineEntry, EngineFormat,
    EngineLimits, EngineProgress, CANCELLED_MESSAGE,
};
pub use extract::{extract_archive, extract_archive_with};
pub use list::{list_archive, list_archive_directory};
pub use mutate::{
    copy_entry_resolved, create_archive_directory, create_archive_file, delete_archive_entries,
    delete_archive_entry, materialize_archive_entry_to_temp, move_entry_resolved,
    rename_archive_entry, should_handle_transfer, MaterializedSource,
};
pub use path::{is_archive_path, is_archive_virtual_path, split_archive_path, ArchivePath};
pub use placeholders::unavailable_archive_note;
