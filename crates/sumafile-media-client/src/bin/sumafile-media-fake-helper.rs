//! A stand-in for the media process, for the client's tests only. What it
//! does with a request is scripted by the requested file's name:
//!
//! - `crash`: the process exits at once, with code 3.
//! - `hang`: it never answers.
//! - `slow<ms>`: it answers after that many milliseconds (300 without a number).
//! - `toolarge`, `undecodable`, `unsupported`, `ioerror`: that failure.
//! - anything else: success, with the body `OK:<file name>`; `png` in the
//!   name makes the reply a PNG.
//!
//! A `video_thumbnail` request that succeeds has the body
//! `OK:<file name>@<positionPercent>` and says who decoded the frame: the
//! graphics card when the name has `gpu`; else the processor, because the
//! request turned the card off, or by name: `nodevice`, `failedhw`, and
//! otherwise for want of a hardware decoder. `nameless` answers as an older
//! process would, without saying who.
//!
//! Each request runs on its own thread, so replies come in any order.
//!
//! Arguments script how it starts:
//!
//! - `--die-while-exists <path>`: while that file exists, the process exits
//!   with code 5 before it says it is running.
//! - `--mute`: it never says it is running.
//! - `--deaf`: it says it is running and then never reads a request.

use std::io::{self, Write};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use sumafile_media_client::protocol::{
    read_message, write_message, Decoder, FailureKind, ImageKind, Method, Request, ResponseHeader,
    SoftwareReason,
};

fn main() {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let has = |flag: &str| arguments.iter().any(|argument| argument == flag);
    let broken = arguments
        .iter()
        .position(|argument| argument == "--die-while-exists")
        .and_then(|at| arguments.get(at + 1));
    if broken.is_some_and(|marker| std::path::Path::new(marker).exists()) {
        std::process::exit(5);
    }
    if has("--mute") {
        sleep_for_good();
    }
    {
        let mut output = io::stdout();
        let _ = write_message(&mut output, &ResponseHeader::ready(), &[]);
        let _ = output.flush();
    }
    if has("--deaf") {
        sleep_for_good();
    }

    let output = Arc::new(Mutex::new(io::stdout()));
    let mut input = io::stdin().lock();
    while let Ok(Some((header, _body))) = read_message(&mut input) {
        let Ok(request) = serde_json::from_slice::<Request>(&header) else {
            continue;
        };
        let output = Arc::clone(&output);
        std::thread::spawn(move || answer(&request, &output));
    }
}

fn sleep_for_good() -> ! {
    loop {
        std::thread::sleep(Duration::from_secs(3600));
    }
}

fn answer(request: &Request, output: &Mutex<io::Stdout>) {
    let name = std::path::Path::new(&request.path)
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();

    if name.contains("crash") {
        std::process::exit(3);
    }
    if name.contains("hang") {
        sleep_for_good();
    }
    if let Some(rest) = name.split("slow").nth(1) {
        let digits: String = rest.chars().take_while(char::is_ascii_digit).collect();
        std::thread::sleep(Duration::from_millis(digits.parse().unwrap_or(300)));
    }

    let failure = [
        ("toolarge", FailureKind::TooLarge, "too large"),
        ("undecodable", FailureKind::Decode, "bad data"),
        ("unsupported", FailureKind::Unsupported, "no decoder"),
        ("ioerror", FailureKind::Io, "cannot read"),
    ]
    .into_iter()
    .find(|(marker, _, _)| name.contains(marker));

    let (header, body) = match failure {
        Some((_, kind, message)) => (
            ResponseHeader::failure(request.id, kind, message),
            Vec::new(),
        ),
        None if request.method == Method::VideoThumbnail && !name.contains("nameless") => {
            let side = request.size.unwrap_or(0);
            let (decoder, reason) = if name.contains("gpu") {
                (Decoder::Hardware, None)
            } else if request.hardware == Some(false) {
                (Decoder::Software, Some(SoftwareReason::TurnedOff))
            } else if name.contains("nodevice") {
                (Decoder::Software, Some(SoftwareReason::NoDevice))
            } else if name.contains("failedhw") {
                (Decoder::Software, Some(SoftwareReason::HardwareFailed))
            } else {
                (Decoder::Software, Some(SoftwareReason::NoHardwareDecoder))
            };
            let percent = request.position_percent.unwrap_or(0);
            (
                ResponseHeader::video(request.id, side, side, decoder, reason),
                format!("OK:{name}@{percent}").into_bytes(),
            )
        }
        None => {
            let side = match request.method {
                Method::Thumbnail | Method::VideoThumbnail => request.size,
                Method::Preview => request.max_dimension,
            }
            .unwrap_or(0);
            let kind = if name.contains("png") {
                ImageKind::Png
            } else {
                ImageKind::Jpeg
            };
            (
                ResponseHeader::success(request.id, kind, side, side),
                format!("OK:{name}").into_bytes(),
            )
        }
    };

    let mut output = output
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let _ = write_message(&mut *output, &header, &body);
    let _ = output.flush();
}
