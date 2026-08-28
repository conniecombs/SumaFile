//! Length-prefixed JSON-RPC frames: `uint32 LE | UTF-8 JSON`.

use crate::MAX_FRAME_BYTES;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FrameError {
    Oversize { length: u32 },
    UnexpectedEof,
    Io(String),
}

impl std::fmt::Display for FrameError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Oversize { length } => {
                write!(f, "frame length {length} exceeds {MAX_FRAME_BYTES}")
            }
            Self::UnexpectedEof => write!(f, "unexpected end of stream"),
            Self::Io(message) => write!(f, "{message}"),
        }
    }
}

impl std::error::Error for FrameError {}

pub fn decode_length(header: [u8; 4]) -> Result<u32, FrameError> {
    let length = u32::from_le_bytes(header);
    if length > MAX_FRAME_BYTES {
        return Err(FrameError::Oversize { length });
    }
    Ok(length)
}

pub fn encode_frame(payload: &[u8]) -> Result<Vec<u8>, FrameError> {
    let length =
        u32::try_from(payload.len()).map_err(|_| FrameError::Oversize { length: u32::MAX })?;
    if length > MAX_FRAME_BYTES {
        return Err(FrameError::Oversize { length });
    }
    let mut frame = Vec::with_capacity(4 + payload.len());
    frame.extend_from_slice(&length.to_le_bytes());
    frame.extend_from_slice(payload);
    Ok(frame)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_and_decodes_small_payload() {
        let payload = br#"{"jsonrpc":"2.0"}"#;
        let frame = encode_frame(payload).unwrap();
        assert_eq!(&frame[..4], &(payload.len() as u32).to_le_bytes());
        assert_eq!(
            decode_length(frame[..4].try_into().unwrap()).unwrap(),
            payload.len() as u32
        );
        assert_eq!(&frame[4..], payload);
    }

    #[test]
    fn rejects_oversize_length_prefix() {
        let header = (MAX_FRAME_BYTES + 1).to_le_bytes();
        assert!(matches!(
            decode_length(header),
            Err(FrameError::Oversize { .. })
        ));
    }
}
