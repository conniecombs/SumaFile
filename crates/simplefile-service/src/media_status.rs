//! What the video process has decoded since the file service started: how
//! many thumbnails the graphics card decoded, how many the processor, and
//! why the processor was last used. Settings shows it, because nothing else
//! can say whether the graphics card is really in use.
//!
//! A thumbnail that comes from the disk cache is not decoded, so it is not
//! counted here.

use serde::Serialize;
use simplefile_core::media::{SoftwareReason, VideoDecoder};
use std::sync::{Mutex, PoisonError};

/// The counts, and why the processor was last used.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct VideoStatus {
    /// Thumbnails whose frame the graphics card decoded.
    pub hardware: u64,
    /// Thumbnails whose frame the processor decoded.
    pub software: u64,
    pub last_software_reason: Option<SoftwareReason>,
}

/// `get_media_status`'s answer, in the app's words.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MediaStatusReply {
    video_hardware: u64,
    video_software: u64,
    last_software_reason: Option<&'static str>,
}

impl VideoStatus {
    pub fn reply(self) -> MediaStatusReply {
        MediaStatusReply {
            video_hardware: self.hardware,
            video_software: self.software,
            last_software_reason: self.last_software_reason.map(reason_word),
        }
    }
}

/// A reason as it is written on the wire and in the log.
pub fn reason_word(reason: SoftwareReason) -> &'static str {
    match reason {
        SoftwareReason::TurnedOff => "turned_off",
        SoftwareReason::NoDevice => "no_device",
        SoftwareReason::NoHardwareDecoder => "no_hardware_decoder",
        SoftwareReason::HardwareFailed => "hardware_failed",
    }
}

#[derive(Default)]
struct Counted {
    status: VideoStatus,
    last_decoder: Option<VideoDecoder>,
}

/// Counts the video thumbnails decoded since the service started.
pub(crate) struct VideoTally {
    counted: Mutex<Counted>,
}

impl VideoTally {
    pub(crate) const fn new() -> Self {
        Self {
            counted: Mutex::new(Counted {
                status: VideoStatus {
                    hardware: 0,
                    software: 0,
                    last_software_reason: None,
                },
                last_decoder: None,
            }),
        }
    }

    /// Count one decode. The answer is a line for the log when this is the
    /// first decode, or the decoder is not the one that made the last: so
    /// the log shows when the graphics card is in use and when it stops
    /// being, without a line for every thumbnail.
    pub(crate) fn record(
        &self,
        decoder: VideoDecoder,
        reason: Option<SoftwareReason>,
    ) -> Option<String> {
        let mut counted = self.counted.lock().unwrap_or_else(PoisonError::into_inner);
        match decoder {
            VideoDecoder::Hardware => counted.status.hardware += 1,
            VideoDecoder::Software => {
                counted.status.software += 1;
                counted.status.last_software_reason = reason;
            }
        }
        let changed = counted.last_decoder != Some(decoder);
        counted.last_decoder = Some(decoder);
        changed.then(|| match (decoder, reason) {
            (VideoDecoder::Hardware, _) => {
                "video thumbnails: the graphics card is decoding".to_string()
            }
            (VideoDecoder::Software, Some(reason)) => format!(
                "video thumbnails: the processor is decoding ({})",
                reason_word(reason)
            ),
            (VideoDecoder::Software, None) => {
                "video thumbnails: the processor is decoding".to_string()
            }
        })
    }

    pub(crate) fn status(&self) -> VideoStatus {
        self.counted
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .status
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_are_counted_by_who_made_them() {
        let tally = VideoTally::new();
        assert_eq!(tally.status(), VideoStatus::default());

        tally.record(VideoDecoder::Hardware, None);
        tally.record(VideoDecoder::Hardware, None);
        tally.record(
            VideoDecoder::Software,
            Some(SoftwareReason::NoHardwareDecoder),
        );

        let status = tally.status();
        assert_eq!((status.hardware, status.software), (2, 1));
    }

    #[test]
    fn the_last_processor_reason_is_kept() {
        let tally = VideoTally::new();
        assert_eq!(tally.status().last_software_reason, None);

        tally.record(VideoDecoder::Software, Some(SoftwareReason::NoDevice));
        tally.record(VideoDecoder::Software, Some(SoftwareReason::TurnedOff));
        // A hardware decode afterwards does not erase why the processor was
        // last used.
        tally.record(VideoDecoder::Hardware, None);

        assert_eq!(
            tally.status().last_software_reason,
            Some(SoftwareReason::TurnedOff)
        );
    }

    #[test]
    fn the_first_use_of_each_decoder_and_each_change_are_logged_once() {
        let tally = VideoTally::new();
        let hardware = || tally.record(VideoDecoder::Hardware, None);
        let software =
            || tally.record(VideoDecoder::Software, Some(SoftwareReason::HardwareFailed));

        assert!(hardware().is_some(), "the first decode");
        assert_eq!(hardware(), None, "the same again says nothing");
        let changed = software().expect("a change is logged");
        assert!(changed.contains("processor") && changed.contains("hardware_failed"));
        assert_eq!(software(), None);
        assert!(hardware().is_some_and(|line| line.contains("graphics card")));
    }

    #[test]
    fn the_reasons_have_the_wire_words() {
        assert_eq!(reason_word(SoftwareReason::TurnedOff), "turned_off");
        assert_eq!(reason_word(SoftwareReason::NoDevice), "no_device");
        assert_eq!(
            reason_word(SoftwareReason::NoHardwareDecoder),
            "no_hardware_decoder"
        );
        assert_eq!(
            reason_word(SoftwareReason::HardwareFailed),
            "hardware_failed"
        );
    }

    #[test]
    fn the_status_is_worded_for_the_app() {
        let tally = VideoTally::new();
        tally.record(VideoDecoder::Hardware, None);
        tally.record(VideoDecoder::Software, Some(SoftwareReason::NoDevice));

        assert_eq!(
            serde_json::to_value(tally.status().reply()).unwrap(),
            serde_json::json!({
                "videoHardware": 1,
                "videoSoftware": 1,
                "lastSoftwareReason": "no_device"
            })
        );
        assert_eq!(
            serde_json::to_value(VideoStatus::default().reply()).unwrap(),
            serde_json::json!({
                "videoHardware": 0,
                "videoSoftware": 0,
                "lastSoftwareReason": null
            })
        );
    }
}
