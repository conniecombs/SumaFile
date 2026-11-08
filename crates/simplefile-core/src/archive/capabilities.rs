use crate::models::{ArchiveCapabilities, ArchiveFormatCapability};

pub fn get_archive_capabilities() -> ArchiveCapabilities {
    let seven_zip_available = super::seven_zip::resolve_seven_zip_binary().is_some();

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
                format: "7z".to_string(),
                extension: ".7z".to_string(),
                can_list: seven_zip_available,
                can_extract: seven_zip_available,
                can_create: seven_zip_available,
                can_modify: seven_zip_available,
                engine: if seven_zip_available {
                    "bundled-or-configured-7zip".to_string()
                } else {
                    "unavailable".to_string()
                },
                note: if seven_zip_available {
                    None
                } else {
                    Some("7-Zip support requires the bundled 7z tool in the app payload.".to_string())
                },
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

    capabilities
}
