//! `sumafile-remote-test-server --root <dir> --info <file> [--legacy-db <path>]`
//!
//! Serves `<dir>` over SFTP on a loopback port, writes the server's
//! [`TestServerInfo`] to `<file>` as JSON, then serves until it is killed.
//! With `--legacy-db` it first writes a 1.0.4 metadata database holding one
//! profile for this server, `legacy-sftp` named "Legacy SFTP". It never
//! writes a credential.

use std::path::PathBuf;
use std::process::ExitCode;

use sumafile_remote_test_server::{write_legacy_metadata_db, TestServer, TestServerInfo};

/// The legacy profile the bin writes with `--legacy-db`.
const LEGACY_PROFILE_ID: &str = "legacy-sftp";
const LEGACY_CREDENTIAL_TARGET: &str = "SumaFile.Remote.legacy-sftp";

struct Args {
    root: PathBuf,
    info: PathBuf,
    legacy_db: Option<PathBuf>,
}

fn parse_args(mut args: impl Iterator<Item = String>) -> Result<Args, String> {
    let mut root = None;
    let mut info = None;
    let mut legacy_db = None;
    while let Some(flag) = args.next() {
        let mut value = || {
            args.next()
                .ok_or_else(|| format!("{flag} needs a value"))
                .map(PathBuf::from)
        };
        match flag.as_str() {
            "--root" => root = Some(value()?),
            "--info" => info = Some(value()?),
            "--legacy-db" => legacy_db = Some(value()?),
            other => return Err(format!("unknown argument: {other}")),
        }
    }
    Ok(Args {
        root: root.ok_or("--root is required")?,
        info: info.ok_or("--info is required")?,
        legacy_db,
    })
}

/// Writes the info beside its final name, then renames it, so a reader
/// polling for the file never sees half of it.
fn write_info(path: &std::path::Path, info: &TestServerInfo) -> Result<(), String> {
    let json = serde_json::to_vec_pretty(info).map_err(|error| error.to_string())?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let partial = path.with_extension("partial");
    std::fs::write(&partial, json).map_err(|error| error.to_string())?;
    std::fs::rename(&partial, path).map_err(|error| error.to_string())
}

async fn run(args: Args) -> Result<(), String> {
    let mut server = TestServer::start(args.root).await?;
    if let Some(legacy_db) = &args.legacy_db {
        let profile = server
            .info()
            .legacy_profile(LEGACY_PROFILE_ID, LEGACY_CREDENTIAL_TARGET);
        write_legacy_metadata_db(legacy_db, &[profile], None)?;
        server.set_legacy_profile_id(Some(LEGACY_PROFILE_ID.to_string()));
    }
    write_info(&args.info, server.info())?;
    // Serve until the process is killed.
    std::future::pending::<()>().await;
    server.stop().await;
    Ok(())
}

fn main() -> ExitCode {
    let args = match parse_args(std::env::args().skip(1)) {
        Ok(args) => args,
        Err(message) => {
            eprintln!("sumafile-remote-test-server: {message}");
            return ExitCode::from(2);
        }
    };
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("sumafile-remote-test-server: cannot start the runtime: {error}");
            return ExitCode::from(1);
        }
    };
    match runtime.block_on(run(args)) {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("sumafile-remote-test-server: {message}");
            ExitCode::from(1)
        }
    }
}
