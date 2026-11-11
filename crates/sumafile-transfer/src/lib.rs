//! Local-file transfer primitives shared by the SumaFile service and the
//! first-party plugin backends: staging, the recovery journal, retries, rate
//! and memory limits, the policy model, path conflicts and operation ids.
//!
//! This is a leaf crate. It must never depend on `simplefile-core` or
//! `simplefile-service` (spec §12), so plugin backends can reuse it.

pub mod ids;
pub mod journal;
pub mod manifest;
pub mod path_conflict;
pub mod paths;
pub mod policy;
pub mod reliability;
pub mod resources;
pub mod staging;
#[cfg(any(test, feature = "test-support"))]
pub mod test_io;
pub mod tuning;
