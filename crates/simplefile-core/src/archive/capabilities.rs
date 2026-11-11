use crate::models::{ArchiveCapabilities, ArchiveFormatCapability};

/// The built-in formats, then every registered engine format, then one
/// unavailable row per first-party plugin format no engine serves.
pub fn get_archive_capabilities() -> ArchiveCapabilities {
    let mut capabilities = ArchiveCapabilities {
        formats: vec![
            ArchiveFormatCapability {
                format: "zip".to_string(),
                extension: ".zip".to_string(),
                can_list: true,
                can_extract: true,
                can_create: true,
                can_modify: true,
                engine: "built-in".to_string(),
                note: None,
            },
            ArchiveFormatCapability {
                format: "tar".to_string(),
                extension: ".tar".to_string(),
                can_list: true,
                can_extract: true,
                can_create: true,
                can_modify: true,
                engine: "built-in".to_string(),
                note: None,
            },
            ArchiveFormatCapability {
                format: "tar.gz".to_string(),
                extension: ".tar.gz".to_string(),
                can_list: true,
                can_extract: true,
                can_create: true,
                can_modify: true,
                engine: "built-in".to_string(),
                note: None,
            },
            ArchiveFormatCapability {
                format: "rar".to_string(),
                extension: ".rar".to_string(),
                can_list: true,
                can_extract: true,
                can_create: false,
                can_modify: false,
                engine: "built-in-unrar".to_string(),
                note: Some("RAR archives can be listed and extracted, but SumaFile does not create or rewrite RAR archives.".to_string()),
            },
        ],
    };

    for (owner, format, _engine_name) in super::engine::registered_formats() {
        capabilities.formats.push(ArchiveFormatCapability {
            format: super::engine::format_label(&format),
            extension: format.extension,
            can_list: format.can_list,
            can_extract: format.can_extract,
            can_create: format.can_create,
            can_modify: format.can_modify,
            engine: format!("plugin:{owner}"),
            note: None,
        });
    }

    for placeholder in super::placeholders::PLUGIN_FORMAT_PLACEHOLDERS {
        if !super::placeholders::is_unserved(placeholder) {
            continue;
        }
        capabilities.formats.push(ArchiveFormatCapability {
            format: placeholder.label.to_string(),
            extension: placeholder.extension.to_string(),
            can_list: false,
            can_extract: false,
            can_create: false,
            can_modify: false,
            engine: "unavailable".to_string(),
            note: Some(placeholder.note.to_string()),
        });
    }

    capabilities
}
