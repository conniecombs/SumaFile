mod create;
mod extract;
mod list;
mod mutate;
mod path;
mod seven_zip;

#[cfg(test)]
mod tests;

pub use create::{create_archive, resolve_rar_binary};
pub use extract::extract_archive;
pub use list::{list_archive, list_archive_directory};
pub use mutate::{
    copy_entry_resolved, create_archive_directory, create_archive_file, delete_archive_entry,
    materialize_archive_entry_to_temp, move_entry_resolved, rename_archive_entry,
    should_handle_transfer, MaterializedSource,
};
pub use path::{is_archive_virtual_path, split_archive_path, ArchivePath};
