//! Pictures decoded outside the core: the formats SumaFile's media process
//! handles (`sumafile-media`). The core knows a provider, not a process: the
//! file service registers one that owns the process, as it registers archive
//! engines. With none registered, these formats are simply unsupported here
//! and the app's fallbacks apply.

use crate::utils::resolve_readable_path;
use std::path::Path;
use std::sync::{Arc, PoisonError, RwLock};

/// The long side of a preview when the caller names none.
const DEFAULT_PREVIEW_DIMENSION: u32 = 2560;
const PREVIEW_DIMENSIONS: std::ops::RangeInclusive<u32> = 64..=8192;

/// An encoded picture for the preview pane.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EncodedImage {
    /// `"jpeg"` or `"png"`.
    pub format: String,
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>,
}

/// Decodes the media-process formats. The messages of its errors are shown
/// to the user as they are.
pub trait MediaProvider: Send + Sync {
    /// A JPEG of `path` that fits a `size` by `size` box.
    fn thumbnail(&self, path: &Path, size: u32) -> Result<Vec<u8>, String>;
    /// `path` at screen size: at most `max_dimension` on its long side.
    fn preview(&self, path: &Path, max_dimension: u32) -> Result<EncodedImage, String>;
}

static PROVIDER: RwLock<Option<Arc<dyn MediaProvider>>> = RwLock::new(None);

/// Make `provider` the one that decodes the media-process formats. Nothing
/// is asked of it until a thumbnail or a preview of such a format is wanted.
pub fn register_media_provider(provider: Arc<dyn MediaProvider>) {
    *PROVIDER.write().unwrap_or_else(PoisonError::into_inner) = Some(provider);
}

pub fn unregister_media_provider() {
    *PROVIDER.write().unwrap_or_else(PoisonError::into_inner) = None;
}

pub fn media_provider() -> Option<Arc<dyn MediaProvider>> {
    PROVIDER
        .read()
        .unwrap_or_else(PoisonError::into_inner)
        .clone()
}

/// Whether a thumbnail of `path` is made by the media process, judged from
/// the name alone. The file service uses it to pick the lane the request
/// waits in: such a thumbnail waits on another process, not on a core.
pub fn thumbnail_uses_media_process(path: &str) -> bool {
    extension_of(Path::new(path))
        .is_some_and(|extension| sumafile_imaging::formats::is_media_process_extension(&extension))
}

fn extension_of(path: &Path) -> Option<String> {
    path.extension()
        .map(|extension| extension.to_string_lossy().to_lowercase())
}

/// The provider for `extension`, when the format is one the media process
/// decodes and a provider is registered.
pub(crate) fn provider_for(extension: &str) -> Option<Arc<dyn MediaProvider>> {
    sumafile_imaging::formats::is_media_process_extension(extension)
        .then(media_provider)
        .flatten()
}

/// A picture of `path` for the preview pane, in a format the media process
/// decodes. `max_dimension` is kept between 64 and 8192; 2560 when absent.
pub fn render_image_preview(
    path: String,
    max_dimension: Option<u32>,
) -> Result<EncodedImage, String> {
    let path_buf = resolve_readable_path(&path)?;
    let extension = extension_of(&path_buf).unwrap_or_default();
    let provider =
        provider_for(&extension).ok_or_else(|| format!("Unsupported image format: {extension}"))?;
    let max_dimension = max_dimension
        .unwrap_or(DEFAULT_PREVIEW_DIMENSION)
        .clamp(*PREVIEW_DIMENSIONS.start(), *PREVIEW_DIMENSIONS.end());
    provider.preview(&path_buf, max_dimension)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::preview::generate_thumbnail;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::{Mutex, MutexGuard};

    /// The provider is one for the whole process: tests that register one
    /// take turns.
    static TURN: Mutex<()> = Mutex::new(());

    pub(crate) struct Registered {
        _turn: MutexGuard<'static, ()>,
    }

    impl Registered {
        pub(crate) fn new(provider: Arc<dyn MediaProvider>) -> Self {
            let turn = TURN
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            register_media_provider(provider);
            Self { _turn: turn }
        }

        fn none() -> Self {
            let turn = TURN
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            unregister_media_provider();
            Self { _turn: turn }
        }
    }

    impl Drop for Registered {
        fn drop(&mut self) {
            unregister_media_provider();
        }
    }

    /// Answers with bytes that name the request, and counts its calls.
    #[derive(Default)]
    pub(crate) struct Counting {
        pub(crate) thumbnails: AtomicUsize,
        pub(crate) previews: AtomicUsize,
        pub(crate) last_dimension: AtomicUsize,
    }

    impl MediaProvider for Counting {
        fn thumbnail(&self, path: &Path, size: u32) -> Result<Vec<u8>, String> {
            self.thumbnails.fetch_add(1, Ordering::SeqCst);
            if path.to_string_lossy().contains("broken") {
                return Err("The image could not be decoded.".to_string());
            }
            Ok(format!("thumbnail of {} at {size}", path.display()).into_bytes())
        }

        fn preview(&self, path: &Path, max_dimension: u32) -> Result<EncodedImage, String> {
            self.previews.fetch_add(1, Ordering::SeqCst);
            self.last_dimension
                .store(max_dimension as usize, Ordering::SeqCst);
            Ok(EncodedImage {
                format: "png".to_string(),
                width: max_dimension,
                height: 1,
                data: format!("preview of {}", path.display()).into_bytes(),
            })
        }
    }

    pub(crate) struct Folder(pub(crate) PathBuf);

    impl Folder {
        pub(crate) fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "sumafile-core-media-{name}-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_or(0, |since| since.as_nanos())
            ));
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        pub(crate) fn file(&self, name: &str) -> String {
            let path = self.0.join(name);
            std::fs::write(&path, b"not really a picture").unwrap();
            path.to_string_lossy().into_owned()
        }
    }

    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn a_media_extension_goes_to_the_provider() {
        let folder = Folder::new("routed");
        let provider = Arc::new(Counting::default());
        let _registered = Registered::new(provider.clone());
        let path = folder.file("art.PSD");

        let bytes = generate_thumbnail(path.clone(), Some(96)).unwrap();

        assert_eq!(bytes, format!("thumbnail of {path} at 96").into_bytes());
        assert_eq!(provider.thumbnails.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn a_media_thumbnail_is_cached_on_disk() {
        let folder = Folder::new("cached");
        let provider = Arc::new(Counting::default());
        let _registered = Registered::new(provider.clone());
        let path = folder.file("photo.cr2");

        let first = generate_thumbnail(path.clone(), Some(96)).unwrap();
        let second = generate_thumbnail(path, Some(96)).unwrap();

        assert_eq!(first, second);
        assert_eq!(
            provider.thumbnails.load(Ordering::SeqCst),
            1,
            "the second came from the cache"
        );
    }

    #[test]
    fn a_failed_media_thumbnail_is_an_error_and_is_not_cached() {
        let folder = Folder::new("failed");
        let provider = Arc::new(Counting::default());
        let _registered = Registered::new(provider.clone());
        let path = folder.file("broken.exr");

        let first = generate_thumbnail(path.clone(), Some(96));
        let second = generate_thumbnail(path, Some(96));

        assert_eq!(first, Err("The image could not be decoded.".to_string()));
        assert!(second.is_err());
        assert_eq!(provider.thumbnails.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn a_built_in_extension_never_reaches_the_provider() {
        let folder = Folder::new("built-in");
        let provider = Arc::new(Counting::default());
        let _registered = Registered::new(provider.clone());
        let path = folder.0.join("photo.png");
        image::RgbImage::from_pixel(8, 8, image::Rgb([1, 2, 3]))
            .save(&path)
            .unwrap();

        let bytes = generate_thumbnail(path.to_string_lossy().into_owned(), Some(64)).unwrap();

        assert!(bytes.starts_with(&[0xFF, 0xD8]), "a JPEG made in the core");
        assert_eq!(provider.thumbnails.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn without_a_provider_the_extension_is_unsupported() {
        let folder = Folder::new("none");
        let _unregistered = Registered::none();
        let path = folder.file("art.psd");

        let thumbnail = generate_thumbnail(path.clone(), Some(64));
        let preview = render_image_preview(path, None);

        assert_eq!(thumbnail, Err("Unsupported image format: psd".to_string()));
        assert_eq!(
            preview.err(),
            Some("Unsupported image format: psd".to_string())
        );
    }

    #[test]
    fn a_format_nobody_decodes_stays_unsupported_with_a_provider() {
        let folder = Folder::new("unknown");
        let provider = Arc::new(Counting::default());
        let _registered = Registered::new(provider.clone());
        let path = folder.file("movie.heic");

        let thumbnail = generate_thumbnail(path.clone(), Some(64));
        let preview = render_image_preview(path, None);

        assert_eq!(thumbnail, Err("Unsupported image format: heic".to_string()));
        assert_eq!(
            preview.err(),
            Some("Unsupported image format: heic".to_string())
        );
        assert_eq!(provider.thumbnails.load(Ordering::SeqCst), 0);
        assert_eq!(provider.previews.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn a_preview_comes_from_the_provider_at_a_bounded_size() {
        let folder = Folder::new("preview");
        let provider = Arc::new(Counting::default());
        let _registered = Registered::new(provider.clone());
        let path = folder.file("scene.exr");
        let dimension = |asked| {
            render_image_preview(path.clone(), asked).unwrap();
            provider.last_dimension.load(Ordering::SeqCst)
        };

        let preview = render_image_preview(path.clone(), Some(1200)).unwrap();

        assert_eq!(preview.format, "png");
        assert_eq!(preview.data, format!("preview of {path}").into_bytes());
        assert_eq!(dimension(Some(1200)), 1200);
        assert_eq!(dimension(None), 2560, "the default");
        assert_eq!(dimension(Some(1)), 64, "never below 64");
        assert_eq!(dimension(Some(1_000_000)), 8192, "never above 8192");
    }

    #[test]
    fn the_lane_question_is_answered_from_the_name_alone() {
        assert!(thumbnail_uses_media_process(r"C:\Art\cover.PSD"));
        assert!(thumbnail_uses_media_process(
            r"C:\Archive.zip\raw\IMG_0001.cr2"
        ));
        assert!(!thumbnail_uses_media_process(r"C:\Photos\IMG_0001.jpg"));
        assert!(!thumbnail_uses_media_process(r"C:\Photos\no-extension"));
    }
}
