use super::parallel::{ActiveFile, TuningUpdate};
use super::resources::{MemoryBudget, RateLimit};
use super::tuning::{self, Controller, Policy};
use std::path::Path;
use std::sync::Arc;
use std::time::{Duration, Instant};
use sumafile_transfer::tuning::{
    lanes_enabled, DriveKind, Lane, LaneReport, Lanes, LatencyHistogram,
};

/// How often the stored policy is re-read while a transfer runs.
const POLICY_REFRESH: Duration = Duration::from_secs(5);

/// Lives for the whole transfer, including short batches of small files.
///
/// The decisions themselves (starting point, sampling, probing, lane shares)
/// are `sumafile_transfer::tuning`; this owns what they need from the machine:
/// the drive kinds, the remembered routes, the live policy and the clock.
pub(crate) struct Session<'a> {
    pub memory: Arc<MemoryBudget>,
    pub limiter: Arc<RateLimit>,
    lanes: Lanes,
    latency: [Arc<LatencyHistogram>; 2],
    routes: [String; 2],
    buffers: [usize; 2],
    /// The slower of the source and destination drive kinds.
    route_kind: DriveKind,
    emit: &'a dyn Fn(TuningUpdate),
    policy: &'a dyn Fn() -> Policy,
    operation_id: &'a str,
    endpoints: String,
    destination: String,
    policy_at: Instant,
    displayed_at: Instant,
}

/// Drive root (`c:\`) or UNC share that a path's volume facts are cached by.
fn volume_root(path: &str) -> String {
    let root = normalized_volume_path(path);
    if root.starts_with("\\\\") {
        root.split('\\').take(4).collect::<Vec<_>>().join("\\")
    } else {
        root.chars().take(3).collect::<String>()
    }
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
        let mut volume = |path: &str| {
            cache
                .entry(volume_root(path))
                .or_insert_with(|| (endpoint(path, &drives), drive_kind(path, &drives)))
                .clone()
        };
        let (destination, destination_kind) = volume(destination);
        let mut route_kind = destination_kind;
        let mut endpoints = Vec::with_capacity(sources.len());
        for source in sources {
            let (identity, kind) = volume(source);
            endpoints.push(identity);
            route_kind = route_kind.slower(kind);
        }
        endpoints.sort();
        endpoints.dedup();
        let initial = policy();
        let now = Instant::now();
        Self {
            memory: MemoryBudget::new(initial.memory_bytes()),
            limiter: Arc::new(RateLimit::new(initial.bandwidth_mib)),
            lanes: Lanes::new(initial, 4 << 20, now),
            latency: [
                Arc::new(LatencyHistogram::default()),
                Arc::new(LatencyHistogram::default()),
            ],
            routes: [String::new(), String::new()],
            buffers: [4 << 20; 2],
            // Tests exercise scheduling, not the disks of the machine they run on.
            route_kind: if cfg!(test) {
                DriveKind::Unknown
            } else {
                route_kind
            },
            emit,
            policy,
            operation_id,
            endpoints: format!("{operation}:{}", endpoints.join("|")),
            destination,
            policy_at: now,
            displayed_at: now,
        }
    }

    /// The policy new files are dispatched with.
    pub fn policy(&self) -> &Policy {
        self.lanes.policy()
    }

    /// A scheduler run starts. Whether small and large files get a lane each
    /// is fixed here for the run, like the queue order.
    pub fn begin(&mut self) {
        let two = lanes_enabled(self.policy());
        self.lanes.begin(two, Instant::now());
    }

    /// Whether this run schedules small and large files in a lane each.
    /// Files map to lanes with `sumafile_transfer::tuning::lane_index`.
    pub fn two_lanes(&self) -> bool {
        self.lanes.two()
    }

    #[cfg(test)]
    pub fn lane_started(&self, lane: usize) -> bool {
        self.lanes.started(lane)
    }

    /// Files this lane may run at once.
    pub fn concurrency(&self, lane: usize) -> usize {
        self.lanes.concurrency(lane)
    }

    /// Files the whole job may run at once, whatever the lanes plan.
    pub fn total_limit(&self) -> usize {
        self.lanes.total_limit()
    }

    /// Where workers record this lane's write latency.
    pub fn latency(&self, lane: usize) -> Arc<LatencyHistogram> {
        self.latency[lane].clone()
    }

    /// Classify the work waiting in `lane` and pick its starting point.
    /// `moved` is what the lane has already copied in this scheduler run.
    pub fn workload(
        &mut self,
        lane: usize,
        bytes: u64,
        files: usize,
        buffer_size: usize,
        moved: u64,
    ) {
        let route = tuning::route_key(&self.endpoints, &self.destination, bytes, files as u64);
        if self.lanes.started(lane)
            && self.routes[lane] == route
            && self.buffers[lane] == buffer_size
        {
            return;
        }
        self.save_lane(lane);
        // The small lane is a stream of small files however few are known yet.
        let files = if self.lanes.two() && lane == Lane::Small.index() {
            files.max(16)
        } else {
            files
        };
        let controller = Controller::new_for_route(
            (self.policy)(),
            buffer_size,
            tuning::recall(&route),
            bytes,
            files,
            self.route_kind,
        );
        self.limiter.update(controller.policy.bandwidth_mib);
        self.memory.update(controller.policy.memory_bytes());
        self.lanes.start(lane, controller, Instant::now(), moved);
        self.routes[lane] = route;
        self.buffers[lane] = buffer_size;
    }

    pub fn observe(&mut self, reports: [LaneReport; 2]) {
        self.observe_at(Instant::now(), reports);
    }

    pub fn observe_at(&mut self, now: Instant, reports: [LaneReport; 2]) {
        let retried = reports.iter().any(|report| report.retries > 0);
        if retried || now.saturating_duration_since(self.policy_at) >= POLICY_REFRESH {
            self.lanes.update_policy((self.policy)(), self.buffers);
            self.limiter.update(self.policy().bandwidth_mib);
            self.memory.update(self.policy().memory_bytes());
            self.policy_at = now;
        }
        self.lanes
            .observe(now, reports, [&self.latency[0], &self.latency[1]]);
    }

    pub fn display(&mut self, files: Vec<ActiveFile>, force: bool) {
        if !force && self.displayed_at.elapsed() < Duration::from_millis(200) {
            return;
        }
        (self.emit)(TuningUpdate {
            operation_id: self.operation_id.into(),
            mode: self.policy().mode,
            concurrency: self.lanes.planned(),
            explanation: self
                .limiter
                .explanation()
                .unwrap_or_else(|| self.lanes.explanation()),
            active_files: files,
        });
        self.displayed_at = Instant::now();
    }

    fn save_lane(&self, lane: usize) {
        if !self.lanes.started(lane)
            || self.routes[lane].is_empty()
            || self.policy().bandwidth_mib != 0
        {
            return;
        }
        if let Some(concurrency) = self.lanes.controller(lane).learned_concurrency() {
            if let Err(error) = tuning::remember(&self.routes[lane], concurrency) {
                log::warn!("Could not save transfer tuning: {error}");
            }
        }
    }

    pub fn save(&self) {
        for lane in Lane::ALL {
            self.save_lane(lane.index());
        }
    }
}

/// `\\server\share\...` in lower case for UNC paths, the path otherwise.
fn normalized_volume_path(path: &str) -> String {
    let normalized = path
        .replace('/', "\\")
        .trim_start_matches("\\\\?\\")
        .to_lowercase();
    normalized
        .strip_prefix("unc\\")
        .map(|s| format!("\\\\{s}"))
        .unwrap_or(normalized)
}

/// What kind of storage a path is on, from the drive list and, for fixed
/// disks, the storage driver's seek-penalty answer (hard disk or not).
fn drive_kind(path: &str, drives: &[simplefile_core::models::DriveInfo]) -> DriveKind {
    let normalized = normalized_volume_path(path);
    if normalized.starts_with("\\\\") {
        return DriveKind::Network;
    }
    let Some(drive) = drives
        .iter()
        .find(|d| normalized.starts_with(&d.path.to_lowercase()))
    else {
        return DriveKind::Unknown;
    };
    let fixed = drive.drive_type == "Fixed";
    // Cloud and FUSE mounts report themselves as fixed disks.
    let virtual_file_system =
        fixed && simplefile_core::utils::is_network_path(Path::new(&drive.path));
    let seek = if fixed && !virtual_file_system {
        seek_penalty(&drive.path)
    } else {
        None
    };
    DriveKind::classify(false, &drive.drive_type, virtual_file_system, seek)
}

/// Whether the disk behind a drive letter reports a seek penalty (a hard
/// disk) or none (solid state). `None` when the driver will not say: some
/// RAID, virtual and USB bridges do not answer.
///
/// The volume is opened with no access rights, which needs no elevation,
/// takes no lock and reads nothing from the disk.
#[cfg(windows)]
fn seek_penalty(drive_root: &str) -> Option<bool> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
    };
    use windows_sys::Win32::System::Ioctl::{
        PropertyStandardQuery, StorageDeviceSeekPenaltyProperty, DEVICE_SEEK_PENALTY_DESCRIPTOR,
        IOCTL_STORAGE_QUERY_PROPERTY, STORAGE_PROPERTY_QUERY,
    };
    use windows_sys::Win32::System::IO::DeviceIoControl;

    let bytes = drive_root.as_bytes();
    if bytes.len() < 2 || !bytes[0].is_ascii_alphabetic() || bytes[1] != b':' {
        return None;
    }
    let device = format!("\\\\.\\{}:", bytes[0] as char);
    let wide: Vec<u16> = std::ffi::OsStr::new(&device)
        .encode_wide()
        .chain(Some(0))
        .collect();
    let handle = unsafe {
        CreateFileW(
            wide.as_ptr(),
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            std::ptr::null(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE || handle.is_null() {
        return None;
    }
    let query = STORAGE_PROPERTY_QUERY {
        PropertyId: StorageDeviceSeekPenaltyProperty,
        QueryType: PropertyStandardQuery,
        AdditionalParameters: [0],
    };
    // Read as plain words: the answer's last field is a byte the driver
    // fills, which is only looked at once the call has succeeded.
    const ANSWER_BYTES: usize = std::mem::size_of::<DEVICE_SEEK_PENALTY_DESCRIPTOR>();
    let mut answer = [0u32; ANSWER_BYTES.div_ceil(4)];
    let mut returned = 0u32;
    let ok = unsafe {
        DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            (&query as *const STORAGE_PROPERTY_QUERY).cast(),
            std::mem::size_of::<STORAGE_PROPERTY_QUERY>() as u32,
            answer.as_mut_ptr().cast(),
            ANSWER_BYTES as u32,
            &mut returned,
            std::ptr::null_mut(),
        )
    };
    unsafe {
        CloseHandle(handle);
    }
    let flag_offset = std::mem::offset_of!(DEVICE_SEEK_PENALTY_DESCRIPTOR, IncursSeekPenalty);
    if ok == 0 || (returned as usize) <= flag_offset {
        return None;
    }
    let word = answer[flag_offset / 4].to_ne_bytes();
    Some(word[flag_offset % 4] != 0)
}

#[cfg(not(windows))]
fn seek_penalty(_: &str) -> Option<bool> {
    None
}

fn endpoint(path: &str, drives: &[simplefile_core::models::DriveInfo]) -> String {
    let normalized = normalized_volume_path(path);
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;
    use std::time::{SystemTime, UNIX_EPOCH};

    /// A destination no other test (and no earlier run) has a remembered
    /// setting for. A UNC name is identified without touching the network.
    fn unique_destination() -> String {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        format!(r"\\sumafile-adaptive-test-{nanos}\share\folder")
    }

    fn report(saturated: bool, retries: u64) -> LaneReport {
        LaneReport {
            bytes: 0,
            saturated,
            demand: true,
            retries,
        }
    }

    #[test]
    fn mixed_work_gets_a_lane_per_size_class_within_the_limit() {
        let updates = Mutex::new(Vec::new());
        let emit = |update: TuningUpdate| updates.lock().unwrap().push(update);
        let policy = Policy::default;
        let destination = unique_destination();
        let mut session = Session::new("lanes-test", "copy", &[], &destination, &policy, &emit);
        session.begin();
        assert!(session.two_lanes());
        assert!(!session.lane_started(0) && !session.lane_started(1));

        session.workload(0, 8 << 30, 4, 4 << 20, 0);
        assert_eq!(session.concurrency(0), 4);
        session.workload(1, 64 * 4096, 64, 4 << 20, 0);
        assert_eq!([session.concurrency(0), session.concurrency(1)], [4, 12]);
        assert_eq!(session.total_limit(), 16);

        session.display(Vec::new(), true);
        {
            let updates = updates.lock().unwrap();
            let shown = updates.last().unwrap();
            assert_eq!(shown.concurrency, 16);
            assert_eq!(
                shown.explanation,
                "4 large + 12 small \u{b7} Scaling concurrency for small-file batch"
            );
        }

        // An I/O retry in the small lane halves that lane only, at once.
        session.observe_at(Instant::now(), [report(true, 0), report(true, 2)]);
        assert_eq!([session.concurrency(0), session.concurrency(1)], [4, 6]);
        session.display(Vec::new(), true);
        let updates = updates.lock().unwrap();
        let shown = updates.last().unwrap();
        assert_eq!(shown.concurrency, 10);
        assert_eq!(
            shown.explanation,
            "4 large + 6 small \u{b7} Reduced concurrency after an I/O retry"
        );
    }

    #[test]
    fn a_custom_limit_or_a_chosen_order_keeps_one_pool() {
        for policy in [
            Policy {
                mode: tuning::Mode::Manual,
                manual_concurrency: 5,
                ..Policy::default()
            },
            Policy {
                order_by: tuning::OrderBy::SizeDesc,
                ..Policy::default()
            },
        ] {
            let source = || policy.clone();
            let destination = unique_destination();
            let mut session =
                Session::new("one-pool-test", "copy", &[], &destination, &source, &|_| {});
            session.begin();
            assert!(!session.two_lanes());
            session.workload(0, 8 << 30, 4, 4 << 20, 0);
            let expected = if policy.mode == tuning::Mode::Manual {
                5
            } else {
                4
            };
            assert_eq!(session.concurrency(0), expected);
            assert!(!session.lane_started(1));
        }
    }

    /// The drive-kind probe must be harmless wherever the tests run; what it
    /// answers depends on the machine and is not asserted.
    #[test]
    fn probing_the_temp_drive_is_harmless_and_unc_paths_are_network() {
        let drives = simplefile_core::drives::list_drives_light().unwrap_or_default();
        let temp = std::env::temp_dir().to_string_lossy().into_owned();
        let first = drive_kind(&temp, &drives);
        assert_eq!(drive_kind(&temp, &drives), first, "the answer is stable");
        assert_eq!(
            drive_kind(r"\\server\share\file.bin", &drives),
            DriveKind::Network
        );
        assert_eq!(
            drive_kind(r"\\?\UNC\server\share\file.bin", &drives),
            DriveKind::Network
        );
        assert_eq!(seek_penalty("not a drive"), None);
        assert_eq!(seek_penalty(""), None);
    }
}
