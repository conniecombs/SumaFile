//! The reference engine: the SumaFile 7-Zip plugin's library driving the
//! bundled 7za, registered with the core as an `ArchiveEngine` so the
//! baseline still runs core validation, staging and size checks, exactly
//! as the service does through the plugin pipe.
use std::path::{Path, PathBuf};

use simplefile_core::archive::{ArchiveEngine, EngineContext, EngineEntry, EngineLimits};
use sumafile_sevenzip_backend::ops::{Limits, SevenZip};
use sumafile_sevenzip_backend::run::RunObserver;

pub struct PluginEngine {
    seven_zip: SevenZip,
}

impl PluginEngine {
    pub fn new(binary: PathBuf) -> Self {
        Self {
            seven_zip: SevenZip::new(binary),
        }
    }
}

/// Relays 7-Zip's percentages to the core context, as the backend's
/// progress bridge relays them to the host, and stops on its cancel flag.
struct ContextObserver<'a, 'b> {
    ctx: Option<&'a EngineContext<'b>>,
    total: u64,
}

impl<'a, 'b> ContextObserver<'a, 'b> {
    fn of(ctx: &'a EngineContext<'b>) -> Self {
        Self {
            ctx: Some(ctx),
            total: 0,
        }
    }

    fn unobserved() -> Self {
        Self {
            ctx: None,
            total: 0,
        }
    }
}

impl RunObserver for ContextObserver<'_, '_> {
    fn total(&mut self, bytes: u64) {
        self.total = bytes;
    }

    fn progress(&mut self, percent: u8, item: Option<&str>) {
        if let Some(ctx) = self.ctx {
            let current = self.total.saturating_mul(u64::from(percent)) / 100;
            let item = item.map(|item| item.replace('\\', "/"));
            ctx.report(current, self.total, item.as_deref());
        }
    }

    fn is_cancelled(&self) -> bool {
        self.ctx.is_some_and(EngineContext::is_cancelled)
    }
}

impl ArchiveEngine for PluginEngine {
    fn name(&self) -> &str {
        "7-Zip"
    }

    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String> {
        Ok(self
            .seven_zip
            .list(archive)?
            .into_iter()
            .map(|entry| EngineEntry {
                path: entry.path.replace('\\', "/"),
                is_dir: entry.is_dir,
                size: entry.size,
                compressed_size: entry.compressed_size,
            })
            .collect())
    }

    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.seven_zip
            .extract_all(archive, destination, &mut ContextObserver::of(ctx))
    }

    fn read_entry(
        &self,
        archive: &Path,
        entry: &str,
        destination: &Path,
        limits: EngineLimits,
    ) -> Result<(), String> {
        self.seven_zip.read_entry(
            archive,
            entry,
            destination,
            Limits {
                max_bytes: limits.max_bytes,
                max_entries: limits.max_entries,
            },
            &mut ContextObserver::unobserved(),
        )
    }

    fn create(
        &self,
        archive: &Path,
        paths: &[PathBuf],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.seven_zip
            .create(archive, paths, &mut ContextObserver::of(ctx))
    }

    fn rename(
        &self,
        archive: &Path,
        entry: &str,
        new_path: &str,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.seven_zip
            .rename(archive, entry, new_path, &mut ContextObserver::of(ctx))
    }

    fn delete(
        &self,
        archive: &Path,
        entries: &[String],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.seven_zip
            .delete(archive, entries, &mut ContextObserver::of(ctx))
    }

    fn add(
        &self,
        archive: &Path,
        base_dir: &Path,
        paths: &[String],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.seven_zip
            .add(archive, base_dir, paths, &mut ContextObserver::of(ctx))
    }
}
