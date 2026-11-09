//! `transfer.run` through the handler: the Transfers row, progress and
//! tuning events, the two cancel ids, the plugin transfer settings and the
//! recovery of a partial a killed backend left behind.

mod common;

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use common::{Harness, HarnessOptions};
use serde_json::{json, Value};
use sumafile_remote_backend::models::{DirectoryListing, FileEntry, RemoteProfile};
use sumafile_remote_backend::provider::{
    RemoteProvider, RemoteProviderFactory, RemoteTransferError,
};
use sumafile_remote_backend::secrets::RemoteSecret;

const MIB: usize = 1024 * 1024;

fn upload_params(session_id: &str, sources: &[String], operation_id: &str) -> Value {
    json!({
        "remoteSessionId": session_id,
        "direction": "upload",
        "sources": sources,
        "destination": "/",
        "conflictAction": "replace",
        "operationId": operation_id
    })
}

fn local_file(harness: &Harness, name: &str, bytes: &[u8]) -> String {
    let path = harness.local.path().join(name);
    std::fs::write(&path, bytes).unwrap();
    path.to_string_lossy().into_owned()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn transfer_run_opens_a_progress_row_reports_and_completes_it() {
    let harness = Harness::start().await;
    let session_id = harness.connect().await;
    let bytes = common::pattern(300 * 1024);
    let source = local_file(&harness, "caf\u{e9} [1].bin", &bytes);

    let outcome = harness
        .ok(
            "transfer.run",
            upload_params(&session_id, &[source], "op-upload-1"),
        )
        .await;
    assert_eq!(outcome["cancelled"], false, "{outcome}");
    assert_eq!(outcome["retry_sources"], json!([]));
    assert_eq!(
        std::fs::read(harness.remote.path().join("caf\u{e9} [1].bin")).unwrap(),
        bytes
    );

    let calls = harness.host.fake_progress_calls();
    assert_eq!(
        calls[0], "begin Uploading 1 item to 127.0.0.1:/",
        "{calls:?}"
    );
    assert!(
        calls
            .iter()
            .any(|call| call.starts_with("report fake-operation-1 ")),
        "{calls:?}"
    );
    let last_report = calls
        .iter()
        .rev()
        .find(|call| call.starts_with("report "))
        .unwrap();
    let full = bytes.len();
    assert!(
        last_report.starts_with(&format!(
            "report fake-operation-1 {full}/{full} 1/1 files · "
        )),
        "{last_report}"
    );
    assert_eq!(calls.last().unwrap(), "complete fake-operation-1");
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn transfer_run_emits_progress_and_tuning_events_with_the_operation_id() {
    let harness = Harness::start().await;
    let session_id = harness.connect().await;
    std::fs::write(harness.remote.path().join("a.txt"), b"alpha").unwrap();
    std::fs::write(harness.remote.path().join("b.txt"), b"beta").unwrap();
    let destination = harness.local.path().to_string_lossy().into_owned();

    let outcome = harness
        .ok(
            "transfer.run",
            json!({
                "remoteSessionId": session_id,
                "direction": "download",
                "sources": ["/a.txt", "/b.txt"],
                "destination": destination,
                "conflictAction": "error",
                "operationId": "op-download-2"
            }),
        )
        .await;
    assert_eq!(outcome["retry_sources"], json!([]), "{outcome}");
    assert_eq!(
        std::fs::read(harness.local.path().join("b.txt")).unwrap(),
        b"beta"
    );
    assert_eq!(
        harness.host.fake_progress_calls()[0],
        "begin Downloading 2 items from 127.0.0.1"
    );

    let events = harness.host.fake_events();
    let progress: Vec<&Value> = events
        .iter()
        .filter(|(name, _)| name == "remote.transfer-progress")
        .map(|(_, params)| params)
        .collect();
    let tuning: Vec<&Value> = events
        .iter()
        .filter(|(name, _)| name == "remote.transfer-tuning")
        .map(|(_, params)| params)
        .collect();
    assert!(!progress.is_empty() && !tuning.is_empty(), "{events:?}");
    assert!(progress
        .iter()
        .chain(tuning.iter())
        .all(|params| params["operationId"] == "op-download-2"));
    let last = progress.last().unwrap();
    assert_eq!(last["status"], "completed");
    assert_eq!(last["current"], 9);
    assert_eq!(last["total"], 9);
    assert_eq!(last["currentFiles"], 2);
    assert_eq!(last["totalFiles"], 2);
    assert!(last.get("currentItem").is_some());
    assert_eq!(tuning[0]["mode"], "automatic");
    assert!(tuning[0]["explanation"].as_str().is_some());
    // The final progress event goes out after the row ended.
    assert_eq!(
        events.last().unwrap().0,
        "remote.transfer-progress",
        "{events:?}"
    );
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn cancel_by_request_id_answers_other_calls_and_cleans_the_partial_file() {
    let harness = Arc::new(Harness::with_settings(&[("bandwidthMib", "1")]).await);
    let session_id = harness.connect().await;
    let source = local_file(&harness, "slow.bin", &common::pattern(4 * MIB));

    let transfer = {
        let harness = harness.clone();
        let params = upload_params(
            &session_id,
            std::slice::from_ref(&source),
            "remote-cancel-test",
        );
        tokio::spawn(async move { harness.ok("transfer.run", params).await })
    };
    harness.wait_for_report().await;

    let started = Instant::now();
    assert_eq!(
        harness
            .ok("profiles.list", Value::Null)
            .await
            .as_array()
            .unwrap()
            .len(),
        1
    );
    assert!(started.elapsed() < Duration::from_secs(1));
    assert!(!transfer.is_finished(), "the upload is still streaming");

    harness.host.fake_cancel("remote-cancel-test");
    let outcome = tokio::time::timeout(Duration::from_secs(5), transfer)
        .await
        .expect("the transfer stops within 5 s of the cancel")
        .unwrap();
    assert_eq!(outcome["cancelled"], true, "{outcome}");
    assert_eq!(outcome["retry_sources"], json!([source]));
    assert_eq!(
        harness.host.fake_progress_calls().last().unwrap(),
        "fail fake-operation-1 Transfer cancelled"
    );
    assert!(
        common::no_partials(harness.remote.path()),
        "{:?}",
        common::names(harness.remote.path())
    );
    assert!(!harness.remote.path().join("slow.bin").exists());
    Arc::try_unwrap(harness).ok().unwrap().stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn cancel_by_progress_row_id_keeps_the_existing_destination_and_leaves_no_partial_file() {
    // The bandwidth limit keeps the 16 MiB download running long enough for
    // the cancel to land mid-stream, however fast the loopback is.
    let harness = Arc::new(Harness::with_settings(&[("bandwidthMib", "1")]).await);
    let session_id = harness.connect().await;
    std::fs::write(
        harness.remote.path().join("large.bin"),
        common::pattern(16 * MIB),
    )
    .unwrap();
    let existing = b"the existing local file".to_vec();
    std::fs::write(harness.local.path().join("large.bin"), &existing).unwrap();
    let destination = harness.local.path().to_string_lossy().into_owned();

    let transfer = {
        let harness = harness.clone();
        let params = json!({
            "remoteSessionId": session_id,
            "direction": "download",
            "sources": ["/large.bin"],
            "destination": destination,
            "conflictAction": "replace",
            "operationId": "op-row-cancel"
        });
        tokio::spawn(async move { harness.ok("transfer.run", params).await })
    };
    harness.wait_for_report().await;
    // The Transfers row's Cancel sends the progress id, not the request id.
    harness.host.fake_cancel("fake-operation-1");

    let outcome = tokio::time::timeout(Duration::from_secs(5), transfer)
        .await
        .expect("the download stops within 5 s of the cancel")
        .unwrap();
    assert_eq!(outcome["cancelled"], true, "{outcome}");
    assert_eq!(
        harness.host.fake_progress_calls().last().unwrap(),
        "fail fake-operation-1 Transfer cancelled"
    );
    assert_eq!(
        std::fs::read(harness.local.path().join("large.bin")).unwrap(),
        existing
    );
    assert_eq!(
        common::names(harness.local.path()),
        vec!["large.bin".to_string()]
    );
    Arc::try_unwrap(harness).ok().unwrap().stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn busy_session_rejects_a_second_operation_while_a_transfer_runs() {
    let harness = Arc::new(Harness::with_settings(&[("bandwidthMib", "1")]).await);
    let session_id = harness.connect().await;
    let source = local_file(&harness, "busy.bin", &common::pattern(4 * MIB));

    let transfer = {
        let harness = harness.clone();
        let params = upload_params(&session_id, &[source], "op-busy");
        tokio::spawn(async move { harness.ok("transfer.run", params).await })
    };
    harness.wait_for_report().await;

    let error = harness
        .err(
            "fs.list",
            json!({ "remoteSessionId": session_id, "path": "/" }),
        )
        .await;
    assert_eq!(
        error,
        "Remote session is busy; wait for or cancel its transfer"
    );

    harness.host.fake_cancel("op-busy");
    let outcome = tokio::time::timeout(Duration::from_secs(5), transfer)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(outcome["cancelled"], true);
    // The session is free again.
    harness
        .ok(
            "fs.list",
            json!({ "remoteSessionId": session_id, "path": "/" }),
        )
        .await;
    Arc::try_unwrap(harness).ok().unwrap().stop().await;
}

/// A provider whose every upload fails with a retryable error.
struct FlakyProvider {
    uploads: Arc<AtomicUsize>,
}

impl RemoteProvider for FlakyProvider {
    fn stat(&mut self, _: &str) -> Result<Option<FileEntry>, String> {
        Ok(None)
    }
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        Ok(DirectoryListing {
            path: path.into(),
            parent: None,
            entries: Vec::new(),
            is_network: true,
        })
    }
    fn create_directory(&mut self, _: &str, _: &str) -> Result<FileEntry, String> {
        Err("not supported".into())
    }
    fn rename_entry(&mut self, _: &str, _: &str) -> Result<FileEntry, String> {
        Err("not supported".into())
    }
    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
        Ok(paths.to_vec())
    }
    fn download(
        &mut self,
        _: &str,
        _: &mut dyn std::io::Write,
    ) -> Result<u64, RemoteTransferError> {
        Err("not supported".into())
    }
    fn upload(
        &mut self,
        path: &str,
        _: &mut dyn std::io::Read,
    ) -> Result<u64, RemoteTransferError> {
        self.uploads.fetch_add(1, Ordering::SeqCst);
        Err(RemoteTransferError::io(
            "Write remote",
            path,
            std::io::Error::from(std::io::ErrorKind::TimedOut),
        ))
    }
}

struct FlakyFactory {
    uploads: Arc<AtomicUsize>,
}

impl RemoteProviderFactory for FlakyFactory {
    fn connect(
        &self,
        _: &RemoteProfile,
        _: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        Ok(Box::new(FlakyProvider {
            uploads: self.uploads.clone(),
        }))
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn transfer_reads_bandwidth_and_retry_settings_from_the_host() {
    // Bandwidth: 4 MiB at 1 MiB/s takes about 4 s.
    let harness = Harness::with_settings(&[("bandwidthMib", "1")]).await;
    let session_id = harness.connect().await;
    let bytes = common::pattern(4 * MIB);
    let source = local_file(&harness, "limited.bin", &bytes);
    let started = Instant::now();
    let outcome = harness
        .ok(
            "transfer.run",
            upload_params(&session_id, &[source], "op-limited"),
        )
        .await;
    let elapsed = started.elapsed();
    assert_eq!(outcome["retry_sources"], json!([]), "{outcome}");
    assert!(elapsed >= Duration::from_secs(3), "{elapsed:?}");
    assert_eq!(
        std::fs::read(harness.remote.path().join("limited.bin")).unwrap(),
        bytes
    );
    harness.stop().await;

    // Retries: two low-level attempts in one pass, 10 ms apart. The defaults
    // (10 attempts, 3 passes, 1 s backoff) would take minutes.
    let uploads = Arc::new(AtomicUsize::new(0));
    let harness = Harness::with(HarnessOptions {
        settings: vec![
            ("lowLevelRetries", "2"),
            ("highLevelRetries", "1"),
            ("retrySleepMs", "10"),
        ],
        factory: Some(Arc::new(FlakyFactory {
            uploads: uploads.clone(),
        })),
        ..HarnessOptions::default()
    })
    .await;
    let session_id = harness.connect().await;
    let source = local_file(&harness, "flaky.bin", b"flaky");
    let started = Instant::now();
    let outcome = harness
        .ok(
            "transfer.run",
            upload_params(&session_id, std::slice::from_ref(&source), "op-flaky"),
        )
        .await;
    assert!(started.elapsed() < Duration::from_secs(5));
    assert_eq!(uploads.load(Ordering::SeqCst), 2);
    assert_eq!(outcome["cancelled"], false);
    assert_eq!(outcome["retry_sources"], json!([source]));
    let last = harness.host.fake_progress_calls().last().unwrap().clone();
    assert!(
        last.starts_with("fail fake-operation-1 1 of 1 selections failed: "),
        "{last}"
    );
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn next_download_recovers_a_partial_left_by_a_killed_backend() {
    use sumafile_transfer::journal::StagingLease;
    use sumafile_transfer::staging::staging_path_for;

    let harness = Harness::start().await;
    let session_id = harness.connect().await;
    std::fs::write(harness.remote.path().join("fresh.txt"), b"fresh").unwrap();

    // A download that a killed backend left behind: its staged file and its
    // ownership journal, never finished or aborted. The journal's lock died
    // with the process, which dropping the lease reproduces. The engine
    // records the canonical destination folder, as the killed one did.
    let folder = std::fs::canonicalize(harness.local.path()).unwrap();
    let stale_target = folder.join("stale.bin");
    let staged = staging_path_for(&stale_target, "op-killed").unwrap();
    {
        let mut lease = StagingLease::begin("/stale.bin", &stale_target, &staged, false).unwrap();
        use std::io::Write;
        lease
            .take_staged_file()
            .expect("the staged file")
            .write_all(b"half a download")
            .unwrap();
    }
    assert!(
        !common::no_partials(harness.local.path()),
        "the stale partial and its journal exist: {:?}",
        common::names(harness.local.path())
    );

    let destination = harness.local.path().to_string_lossy().into_owned();
    let outcome = harness
        .ok(
            "transfer.run",
            json!({
                "remoteSessionId": session_id,
                "direction": "download",
                "sources": ["/fresh.txt"],
                "destination": destination,
                "conflictAction": "replace",
                "operationId": "op-after-kill"
            }),
        )
        .await;
    assert_eq!(outcome["retry_sources"], json!([]), "{outcome}");
    assert_eq!(
        common::names(harness.local.path()),
        vec!["fresh.txt".to_string()]
    );
    assert_eq!(
        std::fs::read(harness.local.path().join("fresh.txt")).unwrap(),
        b"fresh"
    );
    harness.stop().await;
}
