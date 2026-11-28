//! The request loop: read a request, answer it on a thread of its own.
//!
//! The file service's client decides how many requests are here at a time,
//! so the loop does not queue: every request it reads starts at once. A
//! decoder that panics costs its own request only.

use crate::render::RenderError;
use std::io::{Read, Write};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, Mutex, PoisonError};
use sumafile_media_client::protocol::{
    read_message, write_message, FailureKind, Request, ResponseHeader, MAX_BODY_BYTES,
};
use sumafile_media_client::Rendered;

/// Decoders recurse and keep tables on the stack.
const WORKER_STACK_BYTES: usize = 8 * 1024 * 1024;

/// Answer requests from `input` on `output` until `input` ends. Requests
/// still being answered then keep their threads: the caller decides whether
/// to wait for them (a test) or to leave (the process).
pub fn run<R, W, H>(mut input: R, output: W, handler: H)
where
    R: Read,
    W: Write + Send + 'static,
    H: Fn(&Request) -> Result<Rendered, RenderError> + Send + Sync + 'static,
{
    let output = Arc::new(Mutex::new(output));
    let handler = Arc::new(handler);
    while let Ok(Some((header, _body))) = read_message(&mut input) {
        let Ok(request) = serde_json::from_slice::<Request>(&header) else {
            continue;
        };
        let id = request.id;
        let (worker_output, worker_handler) = (Arc::clone(&output), Arc::clone(&handler));
        let started = std::thread::Builder::new()
            .name("media-decode".to_string())
            .stack_size(WORKER_STACK_BYTES)
            .spawn(move || {
                let reply = answer(&request, worker_handler.as_ref());
                write_reply(&worker_output, reply);
            });
        if started.is_err() {
            let busy = RenderError::Io("The media process could not start a decode.".to_string());
            write_reply(&output, (failure(id, &busy), Vec::new()));
        }
    }
}

fn answer<H>(request: &Request, handler: &H) -> (ResponseHeader, Vec<u8>)
where
    H: Fn(&Request) -> Result<Rendered, RenderError>,
{
    let result = catch_unwind(AssertUnwindSafe(|| handler(request))).unwrap_or_else(|_| {
        Err(RenderError::Decode(
            "The image could not be decoded.".to_string(),
        ))
    });
    match result {
        // The wire takes no longer body. With no answer at all, the file
        // service would wait out its timeout and then end the process.
        Ok(rendered) if rendered.bytes.len() > MAX_BODY_BYTES => {
            (failure(request.id, &RenderError::TooLarge), Vec::new())
        }
        Ok(rendered) => (
            ResponseHeader::success(request.id, rendered.kind, rendered.width, rendered.height),
            rendered.bytes,
        ),
        Err(error) => (failure(request.id, &error), Vec::new()),
    }
}

fn failure(id: u64, error: &RenderError) -> ResponseHeader {
    match error {
        RenderError::Unsupported(message) => {
            ResponseHeader::failure(id, FailureKind::Unsupported, message.as_str())
        }
        RenderError::TooLarge => ResponseHeader::failure(
            id,
            FailureKind::TooLarge,
            "The image is too large to decode.",
        ),
        RenderError::Decode(message) => {
            ResponseHeader::failure(id, FailureKind::Decode, message.as_str())
        }
        RenderError::Io(message) => ResponseHeader::failure(id, FailureKind::Io, message.as_str()),
    }
}

fn write_reply<W: Write>(output: &Mutex<W>, (header, body): (ResponseHeader, Vec<u8>)) {
    let mut output = output.lock().unwrap_or_else(PoisonError::into_inner);
    // Nobody is left to tell when the file service has closed the pipe.
    let _ = write_message(&mut *output, &header, &body);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::render::RenderError;
    use std::sync::mpsc;
    use std::time::Duration;
    use sumafile_media_client::protocol::{
        read_message, write_message, FailureKind, ImageKind, Method, Request, ResponseHeader,
        MAX_BODY_BYTES,
    };
    use sumafile_media_client::Rendered;

    /// Collects what the loop writes, and says when a write has happened.
    struct Output(mpsc::Sender<Vec<u8>>);

    impl std::io::Write for Output {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            let _ = self.0.send(bytes.to_vec());
            Ok(bytes.len())
        }

        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    fn requests(paths: &[&str]) -> Vec<u8> {
        let mut wire = Vec::new();
        for (index, path) in paths.iter().enumerate() {
            let request = Request {
                id: index as u64 + 1,
                method: Method::Thumbnail,
                path: (*path).to_string(),
                size: Some(64),
                max_dimension: None,
                position_percent: None,
                hardware: None,
            };
            write_message(&mut wire, &request, &[]).unwrap();
        }
        wire
    }

    /// Runs the loop over `input` and returns every reply, by request id.
    fn replies<H>(input: Vec<u8>, count: usize, handler: H) -> Vec<(ResponseHeader, Vec<u8>)>
    where
        H: Fn(&Request) -> Result<Rendered, RenderError> + Send + Sync + 'static,
    {
        let (sender, written) = mpsc::channel();
        run(input.as_slice(), Output(sender), handler);

        let mut wire = Vec::new();
        let mut replies = Vec::new();
        while replies.len() < count {
            wire.extend(written.recv_timeout(Duration::from_secs(10)).unwrap());
            let mut reader = wire.as_slice();
            replies.clear();
            while let Ok(Some((header, body))) = read_message(&mut reader) {
                replies.push((
                    serde_json::from_slice::<ResponseHeader>(&header).unwrap(),
                    body,
                ));
            }
        }
        replies.sort_by_key(|(header, _)| header.id);
        replies
    }

    fn picture(request: &Request) -> Result<Rendered, RenderError> {
        Ok(Rendered {
            kind: ImageKind::Jpeg,
            width: 64,
            height: 48,
            bytes: request.path.clone().into_bytes(),
        })
    }

    #[test]
    fn every_request_gets_its_own_reply() {
        let replies = replies(requests(&["a.tga", "b.tga"]), 2, picture);

        assert_eq!(
            replies[0].0,
            ResponseHeader::success(1, ImageKind::Jpeg, 64, 48)
        );
        assert_eq!(replies[0].1, b"a.tga");
        assert_eq!(replies[1].0.id, 2);
        assert_eq!(replies[1].1, b"b.tga");
    }

    #[test]
    fn a_failure_carries_its_kind_and_message() {
        let replies = replies(
            requests(&["huge.tga", "bad.tga", "notes.xyz", "gone.tga"]),
            4,
            |request| {
                Err(match request.path.as_str() {
                    "huge.tga" => RenderError::TooLarge,
                    "bad.tga" => RenderError::Decode("not a Targa file".to_string()),
                    "notes.xyz" => RenderError::Unsupported("no decoder".to_string()),
                    _ => RenderError::Io("cannot open".to_string()),
                })
            },
        );

        let kinds: Vec<_> = replies.iter().map(|(header, _)| header.kind).collect();
        assert_eq!(
            kinds,
            [
                Some(FailureKind::TooLarge),
                Some(FailureKind::Decode),
                Some(FailureKind::Unsupported),
                Some(FailureKind::Io)
            ]
        );
        assert_eq!(replies[1].0.error.as_deref(), Some("not a Targa file"));
        assert!(replies
            .iter()
            .all(|(header, body)| !header.ok && body.is_empty()));
    }

    /// The wire takes no body over its limit. A reply that is simply not
    /// sent leaves the file service waiting until its timeout, which ends
    /// the process and blames the file.
    #[test]
    fn a_picture_too_long_for_the_wire_is_answered_as_too_large() {
        let replies = replies(requests(&["vast.tga"]), 1, |request| {
            let mut rendered = picture(request)?;
            rendered.bytes = vec![0; MAX_BODY_BYTES + 1];
            Ok(rendered)
        });

        assert_eq!(replies[0].0.id, 1);
        assert_eq!(replies[0].0.kind, Some(FailureKind::TooLarge));
        assert!(!replies[0].0.ok && replies[0].1.is_empty());
    }

    /// A decoder that panics on one file must not take the process, or the
    /// other requests, with it.
    #[test]
    fn a_panic_in_a_decoder_is_a_decode_failure() {
        let replies = replies(requests(&["panic.tga", "fine.tga"]), 2, |request| {
            assert!(request.path != "panic.tga", "index out of bounds");
            picture(request)
        });

        assert_eq!(replies[0].0.kind, Some(FailureKind::Decode));
        assert!(replies[1].0.ok);
    }

    #[test]
    fn a_header_that_is_not_a_request_is_passed_over() {
        let mut input = Vec::new();
        write_message(&mut input, &serde_json::json!({ "hello": "there" }), &[]).unwrap();
        input.extend(requests(&["a.tga"]));

        let replies = replies(input, 1, picture);

        assert!(replies[0].0.ok);
    }
}
