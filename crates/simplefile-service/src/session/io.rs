use serde::Serialize;
use serde_json::Value;
use simplefile_ipc::frame::{decode_length, FrameError};
use simplefile_ipc::rpc::JsonRpcResponse;
use simplefile_ipc::{MAX_FRAME_BYTES, PREFIX_RESULT_TOO_LARGE};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::sync::{mpsc, oneshot};

pub(super) const OUTBOUND_QUEUE_CAPACITY: usize = 1024;
pub(super) const WRITE_BATCH_LIMIT: usize = 64;
pub(super) const WRITE_BATCH_BYTES: usize = 1024 * 1024;

#[derive(Clone)]
pub(super) struct OutboundSink {
    pub(super) sender: mpsc::Sender<OutboundFrame>,
}

pub(super) struct OutboundFrame {
    pub(super) payload: Vec<u8>,
    pub(super) ack: Option<oneshot::Sender<Result<(), String>>>,
}

impl OutboundSink {
    pub(super) async fn enqueue_payload(&self, payload: Vec<u8>) -> Result<(), String> {
        self.sender
            .send(OutboundFrame { payload, ack: None })
            .await
            .map_err(|_| "IPC writer is closed".to_string())
    }

    pub(super) async fn write_payload(&self, payload: Vec<u8>) -> Result<(), String> {
        let (ack, done) = oneshot::channel();
        self.sender
            .send(OutboundFrame {
                payload,
                ack: Some(ack),
            })
            .await
            .map_err(|_| "IPC writer is closed".to_string())?;
        done.await
            .map_err(|_| "IPC writer closed before confirming write".to_string())?
    }

    pub(super) fn send_payload_blocking(&self, payload: Vec<u8>) -> Result<(), String> {
        self.sender
            .blocking_send(OutboundFrame { payload, ack: None })
            .map_err(|_| "IPC writer is closed".to_string())
    }
}

pub(super) async fn read_frame<R: AsyncRead + Unpin>(
    reader: &mut R,
) -> Result<Vec<u8>, FrameError> {
    let mut header = [0u8; 4];
    if let Err(error) = reader.read_exact(&mut header).await {
        if error.kind() == std::io::ErrorKind::UnexpectedEof {
            return Err(FrameError::UnexpectedEof);
        }
        return Err(FrameError::Io(error.to_string()));
    }
    let length = decode_length(header)?;
    let mut payload = vec![0u8; length as usize];
    reader
        .read_exact(&mut payload)
        .await
        .map_err(|error| FrameError::Io(error.to_string()))?;
    Ok(payload)
}

pub(super) fn spawn_writer<W>(writer: W) -> OutboundSink
where
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (sender, receiver) = mpsc::channel(OUTBOUND_QUEUE_CAPACITY);
    tokio::spawn(async move {
        let _ = writer_loop(writer, receiver).await;
    });
    OutboundSink { sender }
}

pub(super) async fn writer_loop<W>(
    mut writer: W,
    mut receiver: mpsc::Receiver<OutboundFrame>,
) -> Result<(), String>
where
    W: AsyncWrite + Unpin,
{
    while let Some(first) = receiver.recv().await {
        let mut frames = Vec::with_capacity(WRITE_BATCH_LIMIT);
        frames.push(first);

        for _ in 1..WRITE_BATCH_LIMIT {
            match receiver.try_recv() {
                Ok(frame) => frames.push(frame),
                Err(mpsc::error::TryRecvError::Empty) => break,
                Err(mpsc::error::TryRecvError::Disconnected) => break,
            }

            let queued_bytes: usize = frames.iter().map(|frame| frame.payload.len() + 4).sum();
            if queued_bytes >= WRITE_BATCH_BYTES {
                break;
            }
        }

        let result = write_frame_batch(&mut writer, &frames).await;
        for frame in frames {
            if let Some(ack) = frame.ack {
                let _ = ack.send(result.clone());
            }
        }

        result?;
    }

    Ok(())
}

pub(super) async fn write_json<T>(writer: &OutboundSink, value: &T) -> Result<(), String>
where
    T: Serialize,
{
    let payload = encode_json_payload(value)?;
    writer.write_payload(payload).await
}

pub(super) fn encode_json_payload<T>(value: &T) -> Result<Vec<u8>, String>
where
    T: Serialize + ?Sized,
{
    let payload =
        serde_json::to_vec(value).map_err(|error| format!("failed to encode JSON: {error}"))?;
    if payload.len() > MAX_FRAME_BYTES as usize {
        let error = JsonRpcResponse::application_error(
            None,
            format!("{PREFIX_RESULT_TOO_LARGE} result exceeds 80 MiB; use streamed chunks"),
        );
        serde_json::to_vec(&error).map_err(|err| format!("failed to encode oversize error: {err}"))
    } else {
        Ok(payload)
    }
}

pub(super) fn binary_response_id(
    binary_hot_frames: &Arc<AtomicBool>,
    id: &Option<Value>,
) -> Option<i32> {
    if binary_hot_frames.load(Ordering::Relaxed) {
        crate::binary::request_id_i32(id)
    } else {
        None
    }
}

pub(super) async fn write_binary_response(
    writer: &OutboundSink,
    id: Option<Value>,
    payload: &[u8],
) -> Result<(), String> {
    if payload.len() > MAX_FRAME_BYTES as usize {
        let error = JsonRpcResponse::application_error(
            id,
            format!("{PREFIX_RESULT_TOO_LARGE} binary result exceeds 80 MiB; use streamed chunks"),
        );
        write_json(writer, &error).await
    } else {
        writer.write_payload(payload.to_vec()).await
    }
}

pub(super) async fn queue_payload(writer: &OutboundSink, payload: &[u8]) -> Result<(), String> {
    if payload.len() > MAX_FRAME_BYTES as usize {
        return Err(format!(
            "{PREFIX_RESULT_TOO_LARGE} binary frame exceeds 80 MiB"
        ));
    }
    writer.enqueue_payload(payload.to_vec()).await
}

pub(super) async fn write_frame_batch<W>(
    writer: &mut W,
    frames: &[OutboundFrame],
) -> Result<(), String>
where
    W: AsyncWrite + Unpin,
{
    let total_bytes = frames.iter().try_fold(0usize, |acc, frame| {
        validate_payload_length(&frame.payload)?;
        Ok::<usize, String>(acc.saturating_add(frame.payload.len()).saturating_add(4))
    })?;
    let mut batch = Vec::with_capacity(total_bytes);
    for frame in frames {
        append_frame(&mut batch, &frame.payload)?;
    }

    writer
        .write_all(&batch)
        .await
        .map_err(|error| format!("failed to write frame: {error}"))
}

pub(super) fn append_frame(batch: &mut Vec<u8>, payload: &[u8]) -> Result<(), String> {
    validate_payload_length(payload)?;
    let length = u32::try_from(payload.len()).map_err(|_| {
        format!("{PREFIX_RESULT_TOO_LARGE} frame length exceeds supported u32 range")
    })?;
    batch.extend_from_slice(&length.to_le_bytes());
    batch.extend_from_slice(payload);
    Ok(())
}

pub(super) fn validate_payload_length(payload: &[u8]) -> Result<(), String> {
    if payload.len() > MAX_FRAME_BYTES as usize {
        return Err(format!("{PREFIX_RESULT_TOO_LARGE} frame exceeds 80 MiB"));
    }
    Ok(())
}
