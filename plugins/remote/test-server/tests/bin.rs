//! The bin the C# harness and the UI smoke launch.

use std::io::Read;
use std::net::TcpStream;
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use sumafile_remote_test_server::{TestServerInfo, LEGACY_UPDATED_AT, TEST_SERVER_USER};

struct KillOnDrop(Child);

impl Drop for KillOnDrop {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn wait_for_info(path: &Path, child: &mut Child) -> TestServerInfo {
    let deadline = Instant::now() + Duration::from_secs(30);
    loop {
        if let Ok(bytes) = std::fs::read(path) {
            return serde_json::from_slice(&bytes).expect("the info file is complete JSON");
        }
        if let Some(status) = child.try_wait().unwrap() {
            panic!("the test server exited early: {status}");
        }
        assert!(Instant::now() < deadline, "no info file after 30 s");
        std::thread::sleep(Duration::from_millis(50));
    }
}

#[test]
fn bin_writes_its_info_and_a_legacy_database_then_serves() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let info_path = temp.path().join("out").join("server.json");
    let legacy = temp.path().join("legacy").join("metadata.db");

    let child = Command::new(env!("CARGO_BIN_EXE_sumafile-remote-test-server"))
        .arg("--root")
        .arg(&root)
        .arg("--info")
        .arg(&info_path)
        .arg("--legacy-db")
        .arg(&legacy)
        .stdin(Stdio::null())
        .spawn()
        .expect("start the test server");
    let mut child = KillOnDrop(child);
    let info = wait_for_info(&info_path, &mut child.0);

    assert_eq!(info.user, TEST_SERVER_USER);
    assert_eq!(info.password.len(), 16);
    assert!(info.password.chars().all(|c| c.is_ascii_hexdigit()));
    assert!(info.fingerprint.starts_with("SHA256:"));
    assert_eq!(info.root, root);
    assert_eq!(info.legacy_profile_id.as_deref(), Some("legacy-sftp"));
    let json: serde_json::Value =
        serde_json::from_slice(&std::fs::read(&info_path).unwrap()).unwrap();
    assert_eq!(json["legacyProfileId"], "legacy-sftp", "camelCase JSON");

    let connection = rusqlite::Connection::open(&legacy).unwrap();
    let row: (String, String, String, i64, String, Option<String>, Option<String>, String) =
        connection
            .query_row(
                "SELECT id, name, host, port, username, credential_target, trusted_host_fingerprint, updated_at
                 FROM remote_profiles",
                [],
                |row| {
                    Ok((
                        row.get(0)?,
                        row.get(1)?,
                        row.get(2)?,
                        row.get(3)?,
                        row.get(4)?,
                        row.get(5)?,
                        row.get(6)?,
                        row.get(7)?,
                    ))
                },
            )
            .unwrap();
    assert_eq!(
        row,
        (
            "legacy-sftp".to_string(),
            "Legacy SFTP".to_string(),
            "127.0.0.1".to_string(),
            i64::from(info.port),
            TEST_SERVER_USER.to_string(),
            Some("SumaFile.Remote.legacy-sftp".to_string()),
            Some(info.fingerprint.clone()),
            LEGACY_UPDATED_AT.to_string(),
        )
    );
    let settings: i64 = connection
        .query_row("SELECT COUNT(*) FROM settings", [], |row| row.get(0))
        .unwrap();
    assert_eq!(settings, 0, "no transfer.policy row");

    // It serves SSH on the loopback port.
    let mut socket = TcpStream::connect(("127.0.0.1", info.port)).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(10)))
        .unwrap();
    let mut banner = [0u8; 8];
    socket.read_exact(&mut banner).unwrap();
    assert_eq!(&banner, b"SSH-2.0-");
    assert!(
        child.0.try_wait().unwrap().is_none(),
        "it serves until killed"
    );
}

#[test]
fn bin_without_its_arguments_exits_2() {
    let status = Command::new(env!("CARGO_BIN_EXE_sumafile-remote-test-server"))
        .arg("--root")
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .unwrap();
    assert_eq!(status.code(), Some(2));
}
