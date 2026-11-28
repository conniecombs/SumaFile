//! Writes a folder of small uncompressed videos for the UI smoke:
//!
//! ```text
//! write-test-avi <folder> <count>
//! ```
//!
//! The files are `video-001.avi` and so on: 320 by 180, twenty frames at ten
//! a second. They need no codec, so the smoke's result does not depend on
//! what the test machine has installed. See `tests/support/avi.rs`.

#[path = "../tests/support/avi.rs"]
mod avi;

use std::path::PathBuf;
use std::process::ExitCode;

const WIDTH: u32 = 320;
const HEIGHT: u32 = 180;
const FRAMES_PER_SECOND: u32 = 10;
const FRAMES: usize = 20;

fn main() -> ExitCode {
    let mut arguments = std::env::args_os().skip(1);
    let (Some(folder), Some(count)) = (arguments.next(), arguments.next()) else {
        eprintln!("usage: write-test-avi <folder> <count>");
        return ExitCode::from(2);
    };
    let folder = PathBuf::from(folder);
    let Some(count) = count.to_str().and_then(|count| count.parse::<usize>().ok()) else {
        eprintln!("write-test-avi: the count must be a number");
        return ExitCode::from(2);
    };

    if let Err(error) = std::fs::create_dir_all(&folder) {
        eprintln!(
            "write-test-avi: cannot create {}: {error}",
            folder.display()
        );
        return ExitCode::FAILURE;
    }
    for number in 1..=count {
        let path = folder.join(format!("video-{number:03}.avi"));
        if let Err(error) = avi::write(&path, WIDTH, HEIGHT, FRAMES_PER_SECOND, FRAMES) {
            eprintln!("write-test-avi: cannot write {}: {error}", path.display());
            return ExitCode::FAILURE;
        }
    }
    println!("Wrote {count} videos to {}", folder.display());
    ExitCode::SUCCESS
}
