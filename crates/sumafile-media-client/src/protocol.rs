//! The wire format between the file service and the media process, the same
//! in both directions on the process's standard input and output:
//!
//! ```text
//! u32 LE header length | header (UTF-8 JSON) | u32 LE body length | body
//! ```
//!
//! A request has an empty body. A successful reply carries the encoded
//! picture as its body.
//!
//! A `video_thumbnail` request goes to the video process, which speaks the
//! same format. Its successful reply also says who decoded the frame.
//!
//! Before it reads its first request, the media process sends one message
//! with the id 0 and no body: it is running. A process that ends before that
//! never started, and no file is to blame.

use serde::{Deserialize, Serialize};
use std::io::{self, Read, Write};

/// The longest header either end accepts.
pub const MAX_HEADER_BYTES: usize = 64 * 1024;
/// The longest body either end accepts.
pub const MAX_BODY_BYTES: usize = 256 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Method {
    /// A JPEG that fits a `size` by `size` box.
    Thumbnail,
    /// The picture at screen size: at most `maxDimension` on its long side.
    Preview,
    /// A JPEG of one frame of a video, that fits a `size` by `size` box. The
    /// frame is `positionPercent` of the way through. Answered by the video
    /// process (`sumafile-video`).
    #[serde(rename = "video_thumbnail")]
    VideoThumbnail,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Request {
    pub id: u64,
    pub method: Method,
    pub path: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub size: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_dimension: Option<u32>,
    /// For a video thumbnail: how far into the video the frame is, 1 to 99.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position_percent: Option<u8>,
    /// For a video thumbnail: whether the graphics card may decode it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hardware: Option<bool>,
}

/// Who decoded a video frame. `Hardware` means the graphics card decoded it;
/// scaling there after a processor decode does not count.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Decoder {
    Hardware,
    Software,
}

/// Why the processor decoded a video frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SoftwareReason {
    /// The request said not to use the graphics card.
    TurnedOff,
    /// Windows has no graphics device that can decode video.
    NoDevice,
    /// There is a device, and Windows decoded this file on the processor.
    NoHardwareDecoder,
    /// Decoding with the device failed, and without it worked.
    HardwareFailed,
}

/// How the body of a successful reply is encoded.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ImageKind {
    Jpeg,
    Png,
}

impl ImageKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Jpeg => "jpeg",
            Self::Png => "png",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FailureKind {
    /// The media process has no decoder for this file.
    Unsupported,
    /// Decoding would take more memory than the pixel budget allows.
    TooLarge,
    /// The file's contents are not what its format says.
    Decode,
    /// The file could not be read.
    Io,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResponseHeader {
    pub id: u64,
    pub ok: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub format: Option<ImageKind>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub width: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub height: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<FailureKind>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    /// In a successful reply to a video thumbnail: who decoded the frame.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub decoder: Option<Decoder>,
    /// With `decoder: software`: why.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<SoftwareReason>,
}

/// The `id` of the message the media process sends once, before it reads any
/// request, to say that it is running. No request has this id.
pub const READY_ID: u64 = 0;

impl ResponseHeader {
    /// The message that says the process is running.
    pub fn ready() -> Self {
        Self {
            id: READY_ID,
            ok: true,
            format: None,
            width: None,
            height: None,
            kind: None,
            error: None,
            decoder: None,
            reason: None,
        }
    }

    pub fn success(id: u64, format: ImageKind, width: u32, height: u32) -> Self {
        Self {
            id,
            ok: true,
            format: Some(format),
            width: Some(width),
            height: Some(height),
            kind: None,
            error: None,
            decoder: None,
            reason: None,
        }
    }

    /// A video thumbnail: a JPEG, and who decoded its frame.
    pub fn video(
        id: u64,
        width: u32,
        height: u32,
        decoder: Decoder,
        reason: Option<SoftwareReason>,
    ) -> Self {
        Self {
            decoder: Some(decoder),
            reason,
            ..Self::success(id, ImageKind::Jpeg, width, height)
        }
    }

    pub fn failure(id: u64, kind: FailureKind, error: impl Into<String>) -> Self {
        Self {
            id,
            ok: false,
            format: None,
            width: None,
            height: None,
            kind: Some(kind),
            error: Some(error.into()),
            decoder: None,
            reason: None,
        }
    }
}

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

/// Write one message and flush it.
pub fn write_message<W: Write>(
    writer: &mut W,
    header: &impl Serialize,
    body: &[u8],
) -> io::Result<()> {
    let header = serde_json::to_vec(header).map_err(|error| invalid(error.to_string()))?;
    if header.len() > MAX_HEADER_BYTES {
        return Err(invalid("media message header is too long"));
    }
    if body.len() > MAX_BODY_BYTES {
        return Err(invalid("media message body is too long"));
    }
    writer.write_all(&(header.len() as u32).to_le_bytes())?;
    writer.write_all(&header)?;
    writer.write_all(&(body.len() as u32).to_le_bytes())?;
    writer.write_all(body)?;
    writer.flush()
}

/// Read one message: its header bytes and its body. `None` when the input
/// ends before a message starts. An end inside a message is `UnexpectedEof`;
/// a length over its limit is `InvalidData`, before anything is allocated.
pub fn read_message<R: Read>(reader: &mut R) -> io::Result<Option<(Vec<u8>, Vec<u8>)>> {
    let mut length = [0u8; 4];
    let mut filled = 0;
    while filled < length.len() {
        match reader.read(&mut length[filled..]) {
            Ok(0) if filled == 0 => return Ok(None),
            Ok(0) => return Err(io::ErrorKind::UnexpectedEof.into()),
            Ok(read) => filled += read,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
            Err(error) => return Err(error),
        }
    }
    let header = read_block(
        reader,
        u32::from_le_bytes(length),
        MAX_HEADER_BYTES,
        "header",
    )?;

    reader.read_exact(&mut length)?;
    let body = read_block(reader, u32::from_le_bytes(length), MAX_BODY_BYTES, "body")?;
    Ok(Some((header, body)))
}

fn read_block<R: Read>(
    reader: &mut R,
    length: u32,
    limit: usize,
    what: &str,
) -> io::Result<Vec<u8>> {
    let length = length as usize;
    if length > limit {
        return Err(invalid(format!("media message {what} is too long")));
    }
    let mut block = vec![0u8; length];
    reader.read_exact(&mut block)?;
    Ok(block)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The first message of a media process. An older file service has no
    /// request with the id 0, so it reads past it.
    #[test]
    fn the_ready_message_is_id_zero_and_nothing_else() {
        let json = serde_json::to_string(&ResponseHeader::ready()).unwrap();

        assert_eq!(json, r#"{"id":0,"ok":true}"#);
        assert_eq!(READY_ID, 0);
    }

    /// The words a video thumbnail travels in. A request for a picture
    /// carries neither of the two video fields.
    #[test]
    fn a_video_request_and_its_reply_have_these_words() {
        let request = Request {
            id: 7,
            method: Method::VideoThumbnail,
            path: r"C:\v\a.mp4".to_string(),
            size: Some(256),
            max_dimension: None,
            position_percent: Some(25),
            hardware: Some(true),
        };
        let reply = ResponseHeader::video(
            7,
            256,
            144,
            Decoder::Software,
            Some(SoftwareReason::NoHardwareDecoder),
        );

        assert_eq!(
            serde_json::to_string(&request).unwrap(),
            r#"{"id":7,"method":"video_thumbnail","path":"C:\\v\\a.mp4","size":256,"positionPercent":25,"hardware":true}"#
        );
        assert_eq!(
            serde_json::to_string(&reply).unwrap(),
            r#"{"id":7,"ok":true,"format":"jpeg","width":256,"height":144,"decoder":"software","reason":"no_hardware_decoder"}"#
        );
        let hardware = ResponseHeader::video(8, 64, 64, Decoder::Hardware, None);
        assert_eq!(
            serde_json::to_string(&hardware).unwrap(),
            r#"{"id":8,"ok":true,"format":"jpeg","width":64,"height":64,"decoder":"hardware"}"#
        );
        for (reason, word) in [
            (SoftwareReason::TurnedOff, "turned_off"),
            (SoftwareReason::NoDevice, "no_device"),
            (SoftwareReason::NoHardwareDecoder, "no_hardware_decoder"),
            (SoftwareReason::HardwareFailed, "hardware_failed"),
        ] {
            assert_eq!(
                serde_json::to_string(&reason).unwrap(),
                format!("\"{word}\"")
            );
        }
        let picture = serde_json::to_string(&super::tests::request()).unwrap();
        assert!(!picture.contains("positionPercent") && !picture.contains("hardware"));
    }

    fn request() -> Request {
        Request {
            id: 7,
            method: Method::Thumbnail,
            path: r"C:\Photos\été\scan.psd".to_string(),
            size: Some(256),
            max_dimension: None,
            position_percent: None,
            hardware: None,
        }
    }

    #[test]
    fn a_message_round_trips() {
        let mut wire = Vec::new();
        write_message(&mut wire, &request(), b"").unwrap();
        write_message(
            &mut wire,
            &ResponseHeader::success(7, ImageKind::Jpeg, 256, 171),
            b"\xff\xd8picture",
        )
        .unwrap();

        let mut reader = wire.as_slice();
        let (header, body) = read_message(&mut reader).unwrap().unwrap();
        assert_eq!(
            serde_json::from_slice::<Request>(&header).unwrap(),
            request()
        );
        assert!(body.is_empty());
        let (header, body) = read_message(&mut reader).unwrap().unwrap();
        assert_eq!(
            serde_json::from_slice::<ResponseHeader>(&header).unwrap(),
            ResponseHeader::success(7, ImageKind::Jpeg, 256, 171)
        );
        assert_eq!(body, b"\xff\xd8picture");
        assert!(read_message(&mut reader).unwrap().is_none());
    }

    #[test]
    fn the_header_is_camel_case_json() {
        let mut wire = Vec::new();
        write_message(&mut wire, &request(), b"").unwrap();
        let (header, _) = read_message(&mut wire.as_slice()).unwrap().unwrap();
        let header: serde_json::Value = serde_json::from_slice(&header).unwrap();

        assert_eq!(header["method"], "thumbnail");
        assert_eq!(header["size"], 256);
        assert!(header.get("maxDimension").is_none());

        let failure = serde_json::to_value(ResponseHeader::failure(
            3,
            FailureKind::TooLarge,
            "The image is too large",
        ))
        .unwrap();
        assert_eq!(failure["ok"], false);
        assert_eq!(failure["kind"], "too_large");
    }

    #[test]
    fn a_clean_end_reads_as_none() {
        assert!(read_message(&mut [].as_slice()).unwrap().is_none());
    }

    #[test]
    fn an_end_inside_a_message_is_an_error() {
        let mut wire = Vec::new();
        write_message(&mut wire, &request(), b"").unwrap();
        wire.truncate(wire.len() - 3);

        let error = read_message(&mut wire.as_slice()).unwrap_err();

        assert_eq!(error.kind(), std::io::ErrorKind::UnexpectedEof);
    }

    #[test]
    fn an_over_limit_header_is_refused() {
        let mut wire = Vec::new();
        wire.extend_from_slice(&(MAX_HEADER_BYTES as u32 + 1).to_le_bytes());
        wire.extend_from_slice(&[b' '; 16]);

        let error = read_message(&mut wire.as_slice()).unwrap_err();

        assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
    }

    #[test]
    fn an_over_limit_body_is_refused_before_it_is_read() {
        let mut wire = Vec::new();
        wire.extend_from_slice(&2u32.to_le_bytes());
        wire.extend_from_slice(b"{}");
        wire.extend_from_slice(&(MAX_BODY_BYTES as u32 + 1).to_le_bytes());

        let error = read_message(&mut wire.as_slice()).unwrap_err();

        assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
    }
}
