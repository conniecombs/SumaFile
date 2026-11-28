//! The provider the core asks for the formats the media process decodes
//! (`simplefile_core::media`). It owns the client, which owns the process.

use simplefile_core::media::{EncodedImage, MediaProvider};
use std::path::Path;
use std::sync::{Arc, OnceLock};
use sumafile_media_client::{
    default_helper_path, ClientConfig, ClientStats, MediaClient, MediaError,
};

pub struct ClientProvider {
    client: MediaClient,
}

impl ClientProvider {
    /// A provider for the media process `config` names. Nothing starts here.
    pub fn new(config: ClientConfig) -> Self {
        Self {
            client: MediaClient::new(config),
        }
    }

    pub fn stats(&self) -> ClientStats {
        self.client.stats()
    }
}

impl MediaProvider for ClientProvider {
    fn thumbnail(&self, path: &Path, size: u32) -> Result<Vec<u8>, String> {
        self.client
            .thumbnail(path, size)
            .map(|rendered| rendered.bytes)
            .map_err(|error| reported(path, &error))
    }

    fn preview(&self, path: &Path, max_dimension: u32) -> Result<EncodedImage, String> {
        self.client
            .preview(path, max_dimension)
            .map(|rendered| EncodedImage {
                format: rendered.kind.as_str().to_string(),
                width: rendered.width,
                height: rendered.height,
                data: rendered.bytes,
            })
            .map_err(|error| reported(path, &error))
    }
}

fn message(error: &MediaError) -> String {
    error.to_string()
}

/// The error's message, logged when the process itself failed: took too
/// long, stopped, or could not start. A file it simply cannot decode is
/// ordinary and is not logged.
fn reported(path: &Path, error: &MediaError) -> String {
    if matches!(error, MediaError::Failed(_)) {
        log::warn!("media process: {}: {error}", path.display());
    }
    message(error)
}

static INSTALLED: OnceLock<Arc<ClientProvider>> = OnceLock::new();

/// Register the provider for the media process beside this program. The
/// process itself starts with the first thumbnail or preview that needs it.
pub fn install() {
    let provider = INSTALLED.get_or_init(|| {
        Arc::new(ClientProvider::new(ClientConfig::for_helper(
            default_helper_path(),
        )))
    });
    simplefile_core::media::register_media_provider(provider.clone());
}

/// What the installed provider's client has done, for logs and tests.
pub fn installed_stats() -> Option<ClientStats> {
    INSTALLED.get().map(|provider| provider.stats())
}

/// The provider is one for the whole process: tests that register one, or
/// that count on the installed one, take turns.
#[cfg(test)]
pub(crate) static PROVIDER_TURN: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

#[cfg(test)]
mod tests {
    use super::*;
    use simplefile_core::media::MediaProvider;
    use std::path::Path;
    use sumafile_media_client::{ClientConfig, MediaError};

    #[test]
    fn a_client_error_becomes_its_message() {
        assert_eq!(
            message(&MediaError::TooLarge),
            "The image is too large to decode."
        );
        assert_eq!(
            message(&MediaError::Decode(
                "The TIFF file could not be decoded: bad tag".to_string()
            )),
            "The TIFF file could not be decoded: bad tag"
        );
        assert_eq!(
            message(&MediaError::Unsupported),
            "This image cannot be decoded by SumaFile."
        );
    }

    /// A build without the media process beside it must behave as SumaFile
    /// did before: these formats fail here, and the app's fallbacks apply.
    #[test]
    fn without_the_media_process_every_request_fails_at_once_and_starts_nothing() {
        let folder =
            std::env::temp_dir().join(format!("sumafile-service-media-{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        let picture = folder.join("art.psd");
        std::fs::write(&picture, b"8BPS").unwrap();
        let provider =
            ClientProvider::new(ClientConfig::for_helper(folder.join("no-such-program")));

        let thumbnail = provider.thumbnail(&picture, 96);
        let preview = provider.preview(Path::new(&picture), 2560);

        assert_eq!(
            thumbnail,
            Err("This image cannot be decoded by SumaFile.".to_string())
        );
        assert_eq!(
            preview.err(),
            Some("This image cannot be decoded by SumaFile.".to_string())
        );
        assert_eq!(provider.stats().starts, 0);
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn installing_registers_the_provider_and_starts_nothing() {
        let _turn = PROVIDER_TURN.blocking_lock();
        install();
        install();

        assert!(simplefile_core::media::media_provider().is_some());
        assert_eq!(installed_stats().map(|stats| stats.starts), Some(0));
    }
}
