//! Which attempt is made, and what a thumbnail reports about who decoded it.
//!
//! A thumbnail is tried with the graphics device first, when the request
//! allows it and a device exists, and without it second. "Hardware" means
//! one thing only: the decoded frame arrived as a texture on the graphics
//! card. Windows decodes on the processor without any error when it has no
//! hardware decoder for a file, so the attempt alone says nothing.

use sumafile_media_client::protocol::{Decoder, SoftwareReason};

/// Whether Windows has a graphics device that can decode video.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Device {
    Ready,
    Missing,
}

/// One attempt at a thumbnail.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Step {
    WithDevice,
    WithoutDevice,
}

/// The attempt to make first. `hardware`: the request allows the graphics
/// card. When an attempt with the device fails, the one without it follows.
pub fn first_step(hardware: bool, device: Device) -> Step {
    if hardware && device == Device::Ready {
        Step::WithDevice
    } else {
        Step::WithoutDevice
    }
}

/// What the thumbnail an attempt made reports. `step` is the attempt that
/// made it; `first_step_failed`: an attempt with the device came before and
/// failed; `frame_on_graphics_card`: the decoded frame arrived as a texture.
pub fn report(
    hardware: bool,
    device: Device,
    step: Step,
    first_step_failed: bool,
    frame_on_graphics_card: bool,
) -> (Decoder, Option<SoftwareReason>) {
    if step == Step::WithDevice && frame_on_graphics_card {
        return (Decoder::Hardware, None);
    }
    let reason = if !hardware {
        SoftwareReason::TurnedOff
    } else if device == Device::Missing {
        SoftwareReason::NoDevice
    } else if first_step_failed {
        SoftwareReason::HardwareFailed
    } else {
        SoftwareReason::NoHardwareDecoder
    };
    (Decoder::Software, Some(reason))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_first_attempt_uses_the_device_when_it_may_and_can() {
        assert_eq!(first_step(true, Device::Ready), Step::WithDevice);
        assert_eq!(first_step(true, Device::Missing), Step::WithoutDevice);
        assert_eq!(first_step(false, Device::Ready), Step::WithoutDevice);
        assert_eq!(first_step(false, Device::Missing), Step::WithoutDevice);
    }

    #[test]
    fn the_table() {
        // The graphics card decoded the frame.
        assert_eq!(
            report(true, Device::Ready, Step::WithDevice, false, true),
            (Decoder::Hardware, None)
        );
        // A device was given, and Windows decoded on the processor all the same.
        assert_eq!(
            report(true, Device::Ready, Step::WithDevice, false, false),
            (Decoder::Software, Some(SoftwareReason::NoHardwareDecoder))
        );
        // The attempt with the device failed; the one without it worked.
        assert_eq!(
            report(true, Device::Ready, Step::WithoutDevice, true, false),
            (Decoder::Software, Some(SoftwareReason::HardwareFailed))
        );
        // Windows has no graphics device that can decode video.
        assert_eq!(
            report(true, Device::Missing, Step::WithoutDevice, false, false),
            (Decoder::Software, Some(SoftwareReason::NoDevice))
        );
        // The switch is off, whatever the PC has.
        assert_eq!(
            report(false, Device::Ready, Step::WithoutDevice, false, false),
            (Decoder::Software, Some(SoftwareReason::TurnedOff))
        );
        assert_eq!(
            report(false, Device::Missing, Step::WithoutDevice, false, false),
            (Decoder::Software, Some(SoftwareReason::TurnedOff))
        );
    }

    #[test]
    fn a_frame_in_ordinary_memory_never_reports_hardware() {
        for hardware in [false, true] {
            for device in [Device::Ready, Device::Missing] {
                for step in [Step::WithDevice, Step::WithoutDevice] {
                    for first_step_failed in [false, true] {
                        let (decoder, reason) =
                            report(hardware, device, step, first_step_failed, false);

                        assert_eq!(decoder, Decoder::Software);
                        assert!(reason.is_some());
                    }
                }
            }
        }
    }

    /// Without the device no frame is on the card. Should one claim to be,
    /// the report still says software.
    #[test]
    fn only_an_attempt_with_the_device_can_report_hardware() {
        let (decoder, _) = report(true, Device::Ready, Step::WithoutDevice, true, true);

        assert_eq!(decoder, Decoder::Software);
    }
}
