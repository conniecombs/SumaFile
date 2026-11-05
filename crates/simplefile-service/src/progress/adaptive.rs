use super::parallel::{ActiveFile, TuningUpdate};
use super::resources::RateLimit;
use super::tuning::{self, Controller, Policy};
use std::path::Path;
use std::time::{Duration, Instant};

/// Lives for the whole transfer, including short batches of small files.
pub(crate) struct Session<'a> {
    pub controller: Controller,
    pub limiter: std::sync::Arc<RateLimit>,
    emit: &'a dyn Fn(TuningUpdate),
    policy: &'a dyn Fn() -> Policy,
    operation_id: &'a str,
    endpoints: String,
    destination: String,
    route: String,
    buffer_size: usize,
    sampled_at: Instant,
    observed_at: Instant,
    displayed_at: Instant,
    sample_bytes: u64,
    busy: Duration,
    was_busy: bool,
}

impl<'a> Session<'a> {
    pub fn new(
        operation_id: &'a str,
        operation: &str,
        sources: &[String],
        destination: &str,
        policy: &'a dyn Fn() -> Policy,
        emit: &'a dyn Fn(TuningUpdate),
    ) -> Self {
        let drives = simplefile_core::drives::list_drives_light().unwrap_or_default();
        let mut cache = std::collections::HashMap::new();
        let mut identity = |path: &str| {
            let root = path.replace('/', "\\");
            let root = if root.starts_with("\\\\") {
                root.split('\\').take(4).collect::<Vec<_>>().join("\\")
            } else {
                root.chars().take(3).collect::<String>()
            };
            cache
                .entry(root)
                .or_insert_with(|| endpoint(path, &drives))
                .clone()
        };
        let mut endpoints: Vec<_> = sources.iter().map(|s| identity(s)).collect();
        endpoints.sort();
        endpoints.dedup();
        let initial = policy();
        Self {
            limiter: std::sync::Arc::new(RateLimit::new(initial.bandwidth_mib)),
            controller: Controller::new(initial, 4 << 20, None),
            emit,
            policy,
            operation_id,
            endpoints: format!("{operation}:{}", endpoints.join("|")),
            destination: identity(destination),
            route: String::new(),
            buffer_size: 4 << 20,
            sampled_at: Instant::now(),
            observed_at: Instant::now(),
            displayed_at: Instant::now(),
            sample_bytes: 0,
            busy: Duration::ZERO,
            was_busy: false,
        }
    }

    pub fn workload(&mut self, bytes: u64, files: usize, buffer_size: usize, offset: u64) {
        let route = tuning::route_key(&self.endpoints, &self.destination, bytes, files as u64);
        if self.route != route || self.buffer_size != buffer_size {
            self.save();
            self.controller = Controller::new_for_workload(
                (self.policy)(),
                buffer_size,
                tuning::recall(&route),
                bytes,
                files,
            );
            self.limiter.update(self.controller.policy.bandwidth_mib);
            self.route = route;
            self.buffer_size = buffer_size;
            self.sample_bytes = offset;
            self.sampled_at = Instant::now();
            self.observed_at = Instant::now();
            self.busy = Duration::ZERO;
            self.was_busy = false;
        }
    }

    pub fn observe(&mut self, current: u64, saturated: bool, retries: u64) {
        let now = Instant::now();
        if self.was_busy {
            self.busy += now.duration_since(self.observed_at);
        }
        self.observed_at = now;
        self.was_busy = saturated;
        let elapsed = now.duration_since(self.sampled_at);
        if elapsed >= Duration::from_secs(5) || retries > 0 {
            self.controller
                .update_policy((self.policy)(), self.buffer_size);
            self.limiter.update(self.controller.policy.bandwidth_mib);
            self.controller.sample(
                current.saturating_sub(self.sample_bytes),
                elapsed,
                self.busy.as_secs_f64() >= elapsed.as_secs_f64() * 0.85,
                retries,
            );
            self.sample_bytes = current;
            self.sampled_at = now;
            self.busy = Duration::ZERO;
        }
    }

    pub fn display(&mut self, files: Vec<ActiveFile>, force: bool) {
        if !force && self.displayed_at.elapsed() < Duration::from_millis(200) {
            return;
        }
        (self.emit)(TuningUpdate {
            operation_id: self.operation_id.into(),
            mode: self.controller.policy.mode,
            concurrency: self.controller.concurrency,
            explanation: self
                .limiter
                .explanation()
                .unwrap_or_else(|| self.controller.explanation.into()),
            active_files: files,
        });
        self.displayed_at = Instant::now();
    }

    pub fn save(&self) {
        if !self.route.is_empty() && self.controller.policy.bandwidth_mib == 0 {
            if let Some(concurrency) = self.controller.learned_concurrency() {
                if let Err(error) = tuning::remember(&self.route, concurrency) {
                    log::warn!("Could not save transfer tuning: {error}");
                }
            }
        }
    }

    pub fn restart_after_conflict(&mut self, offset: u64) {
        self.controller = Controller::new(
            (self.policy)(),
            self.buffer_size,
            Some(self.controller.concurrency),
        );
        self.sample_bytes = offset;
        self.sampled_at = Instant::now();
        self.observed_at = Instant::now();
        self.busy = Duration::ZERO;
        self.was_busy = false;
    }
}

fn endpoint(path: &str, drives: &[simplefile_core::models::DriveInfo]) -> String {
    let normalized = path
        .replace('/', "\\")
        .trim_start_matches("\\\\?\\")
        .to_lowercase();
    let normalized = normalized
        .strip_prefix("unc\\")
        .map(|s| format!("\\\\{s}"))
        .unwrap_or(normalized);
    if normalized.starts_with("\\\\") {
        return normalized
            .split('\\')
            .filter(|s| !s.is_empty())
            .take(2)
            .collect::<Vec<_>>()
            .join("\\");
    }
    if let Some(drive) = drives
        .iter()
        .find(|d| normalized.starts_with(&d.path.to_lowercase()))
    {
        if let Some(remote) = &drive.remote_path {
            return remote.to_lowercase();
        }
        let serial = volume_serial(Path::new(&drive.path));
        return format!("{}:{}:{serial}", drive.path.to_lowercase(), drive.name);
    }
    // Unknown virtual mounts are kept separate rather than classified as SSDs.
    Path::new(path)
        .components()
        .next()
        .map(|p| p.as_os_str().to_string_lossy().into_owned())
        .unwrap_or_default()
}

#[cfg(windows)]
fn volume_serial(path: &Path) -> u32 {
    use std::os::windows::ffi::OsStrExt;
    let wide: Vec<_> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut serial = 0;
    unsafe {
        windows_sys::Win32::Storage::FileSystem::GetVolumeInformationW(
            wide.as_ptr(),
            std::ptr::null_mut(),
            0,
            &mut serial,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            0,
        );
    }
    serial
}

#[cfg(not(windows))]
fn volume_serial(_: &Path) -> u32 {
    0
}
