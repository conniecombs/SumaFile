//! The provider the core asks for the formats the media process decodes and
//! for video thumbnails (`simplefile_core::media`). It owns two clients, and
//! each owns its process: `sumafile-media` for pictures, `sumafile-video`
//! for video. They are apart so that a video which ends its process, or a
//! Windows without Media Foundation, costs the pictures nothing.

use crate::media_status::{VideoStatus, VideoTally};
use simplefile_core::media::{
    EncodedImage, MediaProvider, SoftwareReason, VideoDecoder, VideoThumbnail,
};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, OnceLock};
use sumafile_media_client::protocol as wire;
use sumafile_media_client::{
    default_helper_path, default_video_helper_path, ClientConfig, ClientStats, MediaClient,
    MediaError,
};

pub struct ClientProvider {
    client: MediaClient,
    video: MediaClient,
    tally: VideoTally,
    /// How many starts without limits have been logged, for each program.
    unlimited_picture_starts_logged: AtomicU64,
    unlimited_video_starts_logged: AtomicU64,
}

impl ClientProvider {
    /// A provider for the media process `pictures` names and the video
    /// process `videos` names. Nothing starts here.
    pub fn new(pictures: ClientConfig, videos: ClientConfig) -> Self {
        Self {
            client: MediaClient::new(pictures),
            video: MediaClient::new(videos),
            tally: VideoTally::new(),
            unlimited_picture_starts_logged: AtomicU64::new(0),
            unlimited_video_starts_logged: AtomicU64::new(0),
        }
    }

    /// What the picture client has done.
    pub fn stats(&self) -> ClientStats {
        self.client.stats()
    }

    /// What the video client has done.
    pub fn video_stats(&self) -> ClientStats {
        self.video.stats()
    }

    /// The video thumbnails decoded since the service started. Read from
    /// the service's own counts: it asks nothing of either client.
    pub fn video_status(&self) -> VideoStatus {
        self.tally.status()
    }

    /// Count one decoded video thumbnail, and log when the decoder in use
    /// has changed.
    fn count(&self, decoder: VideoDecoder, reason: Option<SoftwareReason>) {
        if let Some(line) = self.tally.record(decoder, reason) {
            log::info!("{line}");
        }
    }
}

/// Log each start of a program that Windows refused a job object for. Such
/// a process works as usual, but its memory is not capped. The count is
/// read without waiting on the client: the other program may be starting.
fn log_unlimited_starts(client: &MediaClient, logged: &AtomicU64, program: &str) {
    let unlimited = client.starts_without_limits();
    if unlimited > logged.fetch_max(unlimited, Ordering::Relaxed) {
        log::warn!("{program}: running without its memory limit: Windows refused the job object");
    }
}

fn decoder_of(decoder: wire::Decoder) -> VideoDecoder {
    match decoder {
        wire::Decoder::Hardware => VideoDecoder::Hardware,
        wire::Decoder::Software => VideoDecoder::Software,
    }
}

fn reason_of(reason: wire::SoftwareReason) -> SoftwareReason {
    match reason {
        wire::SoftwareReason::TurnedOff => SoftwareReason::TurnedOff,
        wire::SoftwareReason::NoDevice => SoftwareReason::NoDevice,
        wire::SoftwareReason::NoHardwareDecoder => SoftwareReason::NoHardwareDecoder,
        wire::SoftwareReason::HardwareFailed => SoftwareReason::HardwareFailed,
    }
}

impl MediaProvider for ClientProvider {
    fn video_thumbnail(
        &self,
        path: &Path,
        size: u32,
        position_percent: u8,
        hardware: bool,
    ) -> Result<VideoThumbnail, String> {
        let made = self
            .video
            .video_thumbnail(path, size, position_percent, hardware);
        log_unlimited_starts(
            &self.video,
            &self.unlimited_video_starts_logged,
            "video process",
        );
        let made = made.map_err(|error| reported(path, &error))?;
        let (decoder, reason) = (decoder_of(made.decoder), made.reason.map(reason_of));
        self.count(decoder, reason);
        Ok(VideoThumbnail {
            jpeg: made.rendered.bytes,
            decoder,
            reason,
        })
    }

    fn thumbnail(&self, path: &Path, size: u32) -> Result<Vec<u8>, String> {
        let result = self.client.thumbnail(path, size);
        log_unlimited_starts(
            &self.client,
            &self.unlimited_picture_starts_logged,
            "media process",
        );
        result
            .map(|rendered| rendered.bytes)
            .map_err(|error| reported(path, &error))
    }

    fn preview(&self, path: &Path, max_dimension: u32) -> Result<EncodedImage, String> {
        let result = self.client.preview(path, max_dimension);
        log_unlimited_starts(
            &self.client,
            &self.unlimited_picture_starts_logged,
            "media process",
        );
        result
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

/// The two error-mode flags [`quiet_error_boxes`] turns on.
#[cfg(windows)]
const QUIET_ERRORS: u32 = windows_sys::Win32::System::Diagnostics::Debug::SEM_FAILCRITICALERRORS
    | windows_sys::Win32::System::Diagnostics::Debug::SEM_NOGPFAULTERRORBOX;

/// Ask Windows to report a program that cannot be loaded, or that crashes,
/// as an error and not with a message box. The programs this service starts
/// inherit the setting.
///
/// The video program needs Media Foundation, which some Windows editions
/// ship without. Starting it there fails, and that is handled: video
/// thumbnails fall back to Windows' own. Without this, Windows would also
/// put up a "DLL was not found" box at every attempt. A decoder that
/// crashes is handled too, and must not put up a crash report.
#[cfg(windows)]
fn quiet_error_boxes() {
    use windows_sys::Win32::System::Diagnostics::Debug::{GetErrorMode, SetErrorMode};

    // SAFETY: both calls take and return a plain number.
    unsafe { SetErrorMode(GetErrorMode() | QUIET_ERRORS) };
}

#[cfg(not(windows))]
fn quiet_error_boxes() {}

/// Register the provider for the media and video processes beside this
/// program. Each process starts with the first request that needs it.
pub fn install() {
    quiet_error_boxes();
    let provider = INSTALLED.get_or_init(|| {
        Arc::new(ClientProvider::new(
            ClientConfig::for_helper(default_helper_path()),
            ClientConfig::for_helper(default_video_helper_path()),
        ))
    });
    simplefile_core::media::register_media_provider(provider.clone());
}

/// The video thumbnails the installed provider has decoded since the
/// service started; zeros before one is installed.
pub fn video_status() -> VideoStatus {
    INSTALLED
        .get()
        .map(|provider| provider.video_status())
        .unwrap_or_default()
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
    use crate::media_status::VideoStatus;
    use simplefile_core::media::{MediaProvider, SoftwareReason, VideoDecoder};
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
        let provider = ClientProvider::new(
            ClientConfig::for_helper(folder.join("no-such-program")),
            ClientConfig::for_helper(folder.join("no-such-video-program")),
        );

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

    fn missing(folder: &Path, name: &str) -> ClientConfig {
        ClientConfig::for_helper(folder.join(name))
    }

    /// Video has a program of its own. With neither program there, a video
    /// request fails at once, as these formats did before, and starts nothing.
    #[test]
    fn a_video_goes_to_the_video_program_and_never_the_picture_one() {
        let folder =
            std::env::temp_dir().join(format!("sumafile-service-video-{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        let video = folder.join("clip.mp4");
        std::fs::write(&video, b"not a video").unwrap();
        let provider = ClientProvider::new(
            missing(&folder, "no-picture-program"),
            missing(&folder, "no-video-program"),
        );

        let reply = provider.video_thumbnail(&video, 96, 25, true);

        assert_eq!(
            reply.err(),
            Some("This image cannot be decoded by SumaFile.".to_string())
        );
        assert_eq!(provider.stats().starts, 0, "the picture program");
        assert_eq!(provider.video_stats().starts, 0, "the video program");
        let _ = std::fs::remove_dir_all(folder);
    }

    /// Settings asks while a program may be starting, hung, or missing. The
    /// answer comes from the service's own counts and waits on no client.
    #[test]
    fn the_status_is_answered_without_asking_a_client() {
        let folder = std::env::temp_dir();
        let provider = ClientProvider::new(
            missing(&folder, "no-picture-program"),
            missing(&folder, "no-video-program"),
        );
        assert_eq!(provider.video_status(), VideoStatus::default());

        provider.count(VideoDecoder::Hardware, None);
        provider.count(VideoDecoder::Software, Some(SoftwareReason::NoDevice));

        let status = provider.video_status();
        assert_eq!((status.hardware, status.software), (1, 1));
        assert_eq!(status.last_software_reason, Some(SoftwareReason::NoDevice));
        assert_eq!(provider.video_stats().starts, 0);
    }

    #[test]
    fn a_decoder_and_its_reason_are_carried_across() {
        use sumafile_media_client::protocol as wire;

        assert_eq!(decoder_of(wire::Decoder::Hardware), VideoDecoder::Hardware);
        assert_eq!(decoder_of(wire::Decoder::Software), VideoDecoder::Software);
        for (from, to) in [
            (wire::SoftwareReason::TurnedOff, SoftwareReason::TurnedOff),
            (wire::SoftwareReason::NoDevice, SoftwareReason::NoDevice),
            (
                wire::SoftwareReason::NoHardwareDecoder,
                SoftwareReason::NoHardwareDecoder,
            ),
            (
                wire::SoftwareReason::HardwareFailed,
                SoftwareReason::HardwareFailed,
            ),
        ] {
            assert_eq!(reason_of(from), to);
        }
    }

    /// A program that cannot be loaded must fail quietly: see
    /// `quiet_error_boxes`. The setting belongs to this process, and the
    /// programs it starts inherit it.
    #[cfg(windows)]
    #[test]
    fn error_boxes_are_off_for_this_process_and_the_programs_it_starts() {
        use windows_sys::Win32::System::Diagnostics::Debug::GetErrorMode;

        quiet_error_boxes();

        // SAFETY: the call takes nothing and returns a plain number.
        let mode = unsafe { GetErrorMode() };
        assert_eq!(mode & QUIET_ERRORS, QUIET_ERRORS);
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
