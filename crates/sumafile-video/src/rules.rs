//! The rules of a video thumbnail that need no Windows: which moment of the
//! video, what size to ask for, which way to turn the frame, and when to stop
//! stepping forward.
//!
//! Times are in units of 100 nanoseconds, as Media Foundation counts them.

use std::time::Duration;

/// A thumbnail is never taken closer to the end than this: the last frames
/// of a video are often black, or cannot be decoded.
const SAFE_DISTANCE_FROM_THE_END: u64 = 2_500_000;

/// How long stepping forward from a key frame may take. Past it, the frame
/// reached is used.
pub const STEPPING_LIMIT: Duration = Duration::from_secs(3);

/// The moment of the thumbnail in a video `duration` long: `percent` of the
/// way through (kept between 1 and 99), at most 250 ms before the end. The
/// start when the duration is unknown (0).
pub fn position_for(duration: u64, percent: u8) -> u64 {
    if duration == 0 {
        return 0;
    }
    let percent = f64::from(percent.clamp(1, 99));
    // As the app computed it when it made these thumbnails itself.
    let wanted = (duration as f64 * (percent / 100.0)) as u64;
    wanted.min(duration.saturating_sub(SAFE_DISTANCE_FROM_THE_END))
}

/// The size to ask Windows' video processor for, before the frame is turned.
/// `frame` is the stored size, `pixel_aspect` the shape of one pixel (width
/// to height; a zero counts as square). The result has the frame's shown
/// shape, fits a box of twice `box_side`, and is never larger than the frame
/// as shown.
pub fn processor_size(frame: (u32, u32), pixel_aspect: (u32, u32), box_side: u32) -> (u32, u32) {
    let (width, height) = (u64::from(frame.0.max(1)), frame.1.max(1));
    let (wide, high) = match pixel_aspect {
        (0, _) | (_, 0) => (1, 1),
        (wide, high) => (u64::from(wide), u64::from(high)),
    };
    let shown_width = u32::try_from((width * wide / high).max(1)).unwrap_or(u32::MAX);
    let limit = box_side.saturating_mul(2).max(1);
    if shown_width <= limit && height <= limit {
        return (shown_width, height);
    }
    sumafile_imaging::thumbnail::fit(shown_width, height, limit)
}

/// How far a decoded frame is turned, clockwise, to stand upright.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Turn {
    None,
    Quarter,
    Half,
    ThreeQuarters,
}

impl Turn {
    /// From the rotation a video file states, in degrees. Anything but 90,
    /// 180 and 270 is no turn.
    pub fn from_degrees(degrees: u32) -> Self {
        match degrees {
            90 => Self::Quarter,
            180 => Self::Half,
            270 => Self::ThreeQuarters,
            _ => Self::None,
        }
    }
}

/// Step forward through the frames `next` hands out, each with its time, and
/// return the one to show: the first at or past `target`. A seek lands on
/// the key frame before the position, so the frames before it are decoded
/// only to be dropped.
///
/// `next` gives `None` at the end of the stream; the last frame seen is then
/// used, and `None` comes back when there was none. `elapsed` is the time
/// since stepping began: once it reaches [`STEPPING_LIMIT`], the frame just
/// read is used.
pub fn choose_frame<F, E>(
    mut next: impl FnMut() -> Result<Option<(i64, F)>, E>,
    target: i64,
    mut elapsed: impl FnMut() -> Duration,
) -> Result<Option<F>, E> {
    let mut last = None;
    loop {
        match next()? {
            None => return Ok(last),
            Some((time, frame)) => {
                if time >= target || elapsed() >= STEPPING_LIMIT {
                    return Ok(Some(frame));
                }
                // The frame before is dropped here, not at the end.
                last = Some(frame);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    /// The rule the app used when it made video thumbnails itself
    /// (`VideoThumbnailFrame.PositionFor`): the percent of the duration, at
    /// most 250 ms before the end, and the start when the duration is unknown.
    #[test]
    fn the_position_is_the_apps_rule() {
        for (duration, percent, position) in [
            (0, 25, 0),
            (100_000_000, 25, 25_000_000),
            (100_000_000, 99, 97_500_000),
            (100_000_000, 150, 97_500_000),
            (100_000_000, 0, 1_000_000),
            (1_000_000, 50, 0),
            (36_000_000_000, 33, 11_880_000_000),
        ] {
            assert_eq!(
                position_for(duration, percent),
                position,
                "{percent} % of {duration}"
            );
        }
    }

    /// The video processor is asked for a picture of the frame's shown shape
    /// (non-square pixels change it), twice the thumbnail box on its longer
    /// side as picture thumbnails are, and never larger than the frame.
    #[test]
    fn the_processor_is_asked_for_twice_the_box_and_never_more_than_the_frame() {
        for (frame, pixel_aspect, box_side, size) in [
            ((1920, 1080), (1, 1), 256, (512, 288)),
            ((720, 576), (16, 15), 256, (512, 384)),
            ((320, 240), (1, 1), 256, (320, 240)),
            ((7680, 4320), (1, 1), 96, (192, 108)),
            ((1, 1), (1, 1), 256, (1, 1)),
            ((1080, 1920), (0, 0), 256, (288, 512)),
        ] {
            assert_eq!(
                processor_size(frame, pixel_aspect, box_side),
                size,
                "{frame:?} with pixels of {pixel_aspect:?} in a box of {box_side}"
            );
        }
    }

    #[test]
    fn a_turn_is_read_from_its_degrees() {
        assert_eq!(Turn::from_degrees(0), Turn::None);
        assert_eq!(Turn::from_degrees(90), Turn::Quarter);
        assert_eq!(Turn::from_degrees(180), Turn::Half);
        assert_eq!(Turn::from_degrees(270), Turn::ThreeQuarters);
        assert_eq!(Turn::from_degrees(45), Turn::None);
    }

    /// What a reader hands out for a stream whose frames have these times.
    type Reader<'a> = Box<dyn FnMut() -> Result<Option<(i64, i64)>, String> + 'a>;

    /// A reader over frames at `times` (each frame is its own time), and how
    /// many frames were asked of it.
    fn frames(times: &[i64]) -> (Reader<'_>, &'static Cell<usize>) {
        let asked: &'static Cell<usize> = Box::leak(Box::new(Cell::new(0)));
        let next = move || {
            let index = asked.get();
            asked.set(index + 1);
            Ok(times.get(index).map(|&time| (time, time)))
        };
        (Box::new(next), asked)
    }

    const NO_TIME: fn() -> Duration = || Duration::ZERO;

    #[test]
    fn stepping_stops_at_the_first_frame_at_or_past_the_position() {
        let (next, asked) = frames(&[0, 10, 20, 30, 40]);

        let frame = choose_frame(next, 20, NO_TIME);

        assert_eq!(frame, Ok(Some(20)));
        assert_eq!(asked.get(), 3, "nothing past the frame was decoded");

        let (next, _) = frames(&[0, 10, 20, 30, 40]);
        assert_eq!(choose_frame(next, 15, NO_TIME), Ok(Some(20)));
        let (next, _) = frames(&[7, 17]);
        assert_eq!(choose_frame(next, 0, NO_TIME), Ok(Some(7)));
    }

    /// A file whose duration is wrong ends before the position asked for.
    #[test]
    fn at_the_end_of_the_stream_the_last_frame_is_used() {
        let (next, _) = frames(&[0, 10, 20]);

        assert_eq!(choose_frame(next, 500, NO_TIME), Ok(Some(20)));
    }

    #[test]
    fn a_stream_with_no_frame_gives_none() {
        let (next, _) = frames(&[]);

        assert_eq!(choose_frame(next, 0, NO_TIME), Ok(None));
    }

    /// Key frames far apart, on a slow PC: after three seconds the thumbnail
    /// is the frame reached, a little earlier than asked.
    #[test]
    fn after_three_seconds_the_frame_just_read_is_used() {
        let (next, asked) = frames(&[0, 10, 20, 30, 40]);
        let calls = Cell::new(0u64);
        let clock = || {
            calls.set(calls.get() + 1);
            Duration::from_millis(calls.get() * 1600)
        };

        let frame = choose_frame(next, 1000, clock);

        // 1.6 s after the first frame, 3.2 s after the second.
        assert_eq!(frame, Ok(Some(10)));
        assert_eq!(asked.get(), 2);
    }

    #[test]
    fn an_error_from_the_reader_is_passed_on() {
        let mut calls = 0;
        let next = || {
            calls += 1;
            if calls == 1 {
                Ok(Some((0, 0)))
            } else {
                Err("the drive went away".to_string())
            }
        };

        assert_eq!(
            choose_frame(next, 100, NO_TIME),
            Err("the drive went away".to_string())
        );
    }
}
