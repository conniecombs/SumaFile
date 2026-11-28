//! How much a single decode may ask for.

use crate::render::RenderError;

/// The most memory one decoded picture may take: its width times its height
/// times its channels times the bytes of a sample.
pub const PIXEL_BUDGET_BYTES: u64 = 1 << 30;

/// `Err(TooLarge)` when a picture of this size would not fit the budget.
/// Decoders call it with what the file's header says, before they read or
/// allocate pixels.
pub fn check_budget(
    width: u32,
    height: u32,
    channels: u8,
    bytes_per_sample: u8,
) -> Result<(), RenderError> {
    let bytes = u64::from(width)
        .saturating_mul(u64::from(height))
        .saturating_mul(u64::from(channels))
        .saturating_mul(u64::from(bytes_per_sample));
    if bytes > PIXEL_BUDGET_BYTES {
        return Err(RenderError::TooLarge);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_budget_counts_every_byte_of_every_sample() {
        // 16,384 by 16,384 RGBA at 8 bits is exactly the budget.
        assert!(check_budget(16_384, 16_384, 4, 1).is_ok());
        assert_eq!(
            check_budget(16_384, 16_385, 4, 1),
            Err(RenderError::TooLarge)
        );
        assert_eq!(
            check_budget(16_384, 16_384, 4, 2),
            Err(RenderError::TooLarge)
        );
        assert_eq!(
            check_budget(u32::MAX, u32::MAX, 4, 4),
            Err(RenderError::TooLarge)
        );
    }
}
