//! Small native byte-scanning and case-folding helpers for backend hot paths.
//!
//! Public functions are safe and preserve existing Rust semantics:
//! - case-insensitive contains matches `to_lowercase().contains(...)` for
//!   non-ASCII, and ASCII case folding for pure-ASCII inputs
//! - sort case-folding matches `str::to_lowercase()` for ordering keys
//!
//! Architecture-specific SIMD/assembly stays private and falls back to
//! portable Rust on non-x86_64 targets. On x86_64, SSE2 is baseline; AVX2 is
//! used when runtime-detected.

/// Case-insensitive substring search used by content search.
pub fn contains_case_insensitive(haystack: &str, needle: &str) -> bool {
    if needle.is_empty() {
        return true;
    }

    if haystack.is_ascii() && needle.is_ascii() {
        return contains_ascii_case_insensitive(haystack.as_bytes(), needle.as_bytes());
    }

    haystack.to_lowercase().contains(&needle.to_lowercase())
}

/// True when `bytes` contains a NUL byte (binary sniffing for text tools).
pub fn contains_zero_byte(bytes: &[u8]) -> bool {
    find_byte(bytes, 0).is_some()
}

/// Case-fold `s` for case-insensitive sort keys and comparisons.
///
/// Pure ASCII uses a SIMD/fast lowercase path that matches Unicode lowercase
/// for `A`–`Z`. Non-ASCII uses `str::to_lowercase()` for full correctness
/// (e.g. Turkish `İ`, German `ß`).
pub fn case_fold_for_sort(s: &str) -> String {
    if s.is_empty() {
        return String::new();
    }
    if s.is_ascii() {
        let mut buf = s.as_bytes().to_vec();
        ascii_lowercase_in_place(&mut buf);
        // SAFETY: ASCII input remains valid UTF-8 after A–Z → a–z folding.
        unsafe { String::from_utf8_unchecked(buf) }
    } else {
        s.to_lowercase()
    }
}

/// Cached sort key: directories first, then case-folded name.
#[inline]
pub fn dirs_first_name_key(is_dir: bool, name: &str) -> (bool, String) {
    (!is_dir, case_fold_for_sort(name))
}

fn contains_ascii_case_insensitive(haystack: &[u8], needle: &[u8]) -> bool {
    debug_assert!(!needle.is_empty());
    debug_assert!(haystack.is_ascii());
    debug_assert!(needle.is_ascii());

    if needle.len() > haystack.len() {
        return false;
    }

    let mut offset = 0usize;
    while offset + needle.len() <= haystack.len() {
        let Some(relative) = find_ascii_case_byte(&haystack[offset..], needle[0]) else {
            return false;
        };
        let candidate = offset + relative;
        if candidate + needle.len() > haystack.len() {
            return false;
        }
        if haystack[candidate..candidate + needle.len()].eq_ignore_ascii_case(needle) {
            return true;
        }
        offset = candidate + 1;
    }

    false
}

fn find_ascii_case_byte(bytes: &[u8], byte: u8) -> Option<usize> {
    let lower = byte.to_ascii_lowercase();
    let upper = byte.to_ascii_uppercase();
    if lower == upper {
        find_byte(bytes, lower)
    } else {
        find_first_of_two(bytes, lower, upper)
    }
}

fn find_byte(bytes: &[u8], byte: u8) -> Option<usize> {
    find_first_of_two(bytes, byte, byte)
}

fn find_first_of_two(bytes: &[u8], first: u8, second: u8) -> Option<usize> {
    #[cfg(target_arch = "x86_64")]
    {
        // SAFETY: SIMD readers only access `bytes.len()` bytes and return an
        // in-range offset or `None`.
        unsafe { find_first_of_two_x64(bytes, first, second) }
    }

    #[cfg(not(target_arch = "x86_64"))]
    {
        find_first_of_two_portable(bytes, first, second)
    }
}

#[cfg(not(target_arch = "x86_64"))]
fn find_first_of_two_portable(bytes: &[u8], first: u8, second: u8) -> Option<usize> {
    bytes
        .iter()
        .position(|&byte| byte == first || byte == second)
}

fn ascii_lowercase_in_place(bytes: &mut [u8]) {
    #[cfg(target_arch = "x86_64")]
    {
        // SAFETY: only mutates caller-owned ASCII bytes in place.
        unsafe { ascii_lowercase_in_place_x64(bytes) }
    }

    #[cfg(not(target_arch = "x86_64"))]
    {
        ascii_lowercase_in_place_portable(bytes);
    }
}

#[cfg(not(target_arch = "x86_64"))]
fn ascii_lowercase_in_place_portable(bytes: &mut [u8]) {
    for b in bytes {
        if b.is_ascii_uppercase() {
            *b = b.to_ascii_lowercase();
        }
    }
}

// ---------------------------------------------------------------------------
// x86_64: SSE2 (baseline) + optional AVX2 scanners / case-folders
// ---------------------------------------------------------------------------

#[cfg(target_arch = "x86_64")]
unsafe fn find_first_of_two_x64(bytes: &[u8], first: u8, second: u8) -> Option<usize> {
    if bytes.is_empty() {
        return None;
    }

    if is_x86_feature_detected!("avx2") {
        // SAFETY: AVX2 was runtime-detected.
        return unsafe { find_first_of_two_avx2(bytes, first, second) };
    }

    // SAFETY: SSE2 is part of the x86_64 baseline.
    unsafe { find_first_of_two_sse2(bytes, first, second) }
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "sse2")]
unsafe fn find_first_of_two_sse2(bytes: &[u8], first: u8, second: u8) -> Option<usize> {
    use core::arch::x86_64::{
        __m128i, _mm_cmpeq_epi8, _mm_loadu_si128, _mm_movemask_epi8, _mm_or_si128, _mm_set1_epi8,
    };

    let len = bytes.len();
    let ptr = bytes.as_ptr();
    let mut offset = 0usize;

    let n1 = _mm_set1_epi8(first as i8);
    let n2 = _mm_set1_epi8(second as i8);

    while offset + 16 <= len {
        // SAFETY: `offset + 16 <= len` guarantees a full 16-byte load.
        let chunk = unsafe { _mm_loadu_si128(ptr.add(offset) as *const __m128i) };
        let eq1 = _mm_cmpeq_epi8(chunk, n1);
        let eq2 = _mm_cmpeq_epi8(chunk, n2);
        let mask = _mm_movemask_epi8(_mm_or_si128(eq1, eq2));
        if mask != 0 {
            return Some(offset + mask.trailing_zeros() as usize);
        }
        offset += 16;
    }

    find_first_of_two_scalar_tail(bytes, offset, first, second)
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2")]
unsafe fn find_first_of_two_avx2(bytes: &[u8], first: u8, second: u8) -> Option<usize> {
    use core::arch::x86_64::{
        __m256i, _mm256_cmpeq_epi8, _mm256_loadu_si256, _mm256_movemask_epi8, _mm256_or_si256,
        _mm256_set1_epi8,
    };

    let len = bytes.len();
    let ptr = bytes.as_ptr();
    let mut offset = 0usize;

    let n1 = _mm256_set1_epi8(first as i8);
    let n2 = _mm256_set1_epi8(second as i8);

    while offset + 32 <= len {
        // SAFETY: `offset + 32 <= len` guarantees a full 32-byte load.
        let chunk = unsafe { _mm256_loadu_si256(ptr.add(offset) as *const __m256i) };
        let eq1 = _mm256_cmpeq_epi8(chunk, n1);
        let eq2 = _mm256_cmpeq_epi8(chunk, n2);
        let mask = _mm256_movemask_epi8(_mm256_or_si256(eq1, eq2));
        if mask != 0 {
            return Some(offset + mask.trailing_zeros() as usize);
        }
        offset += 32;
    }

    // SSE2 handles a possible 16-byte middle chunk + scalar tail.
    // SAFETY: SSE2 is baseline on x86_64.
    if let Some(rel) = unsafe { find_first_of_two_sse2(&bytes[offset..], first, second) } {
        return Some(offset + rel);
    }
    None
}

#[cfg(target_arch = "x86_64")]
fn find_first_of_two_scalar_tail(
    bytes: &[u8],
    start: usize,
    first: u8,
    second: u8,
) -> Option<usize> {
    let mut i = start;
    while i < bytes.len() {
        let b = bytes[i];
        if b == first || b == second {
            return Some(i);
        }
        i += 1;
    }
    None
}

#[cfg(target_arch = "x86_64")]
unsafe fn ascii_lowercase_in_place_x64(bytes: &mut [u8]) {
    if bytes.is_empty() {
        return;
    }

    if is_x86_feature_detected!("avx2") {
        // SAFETY: AVX2 was runtime-detected.
        unsafe { ascii_lowercase_in_place_avx2(bytes) };
        return;
    }

    // SAFETY: SSE2 is part of the x86_64 baseline.
    unsafe { ascii_lowercase_in_place_sse2(bytes) };
}

/// Fold `A`–`Z` to `a`–`z` in place using SSE2.
/// Signed compares are valid for ASCII byte values 0–127.
#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "sse2")]
unsafe fn ascii_lowercase_in_place_sse2(bytes: &mut [u8]) {
    use core::arch::x86_64::{
        __m128i, _mm_and_si128, _mm_andnot_si128, _mm_cmpgt_epi8, _mm_loadu_si128, _mm_or_si128,
        _mm_set1_epi8, _mm_storeu_si128,
    };

    let len = bytes.len();
    let ptr = bytes.as_mut_ptr();
    let mut offset = 0usize;

    // ASCII A–Z: b >= 'A' && b <= 'Z'
    let am1 = _mm_set1_epi8(0x40); // 'A' - 1
    let z = _mm_set1_epi8(0x5A); // 'Z'
    let lower_bit = _mm_set1_epi8(0x20);
    let all_ones = _mm_set1_epi8(-1i8);

    while offset + 16 <= len {
        // SAFETY: full 16-byte window inside `bytes`.
        let chunk = unsafe { _mm_loadu_si128(ptr.add(offset) as *const __m128i) };
        // b > 'A'-1  ⇒ b >= 'A'
        let ge_a = _mm_cmpgt_epi8(chunk, am1);
        // b > 'Z'
        let gt_z = _mm_cmpgt_epi8(chunk, z);
        // b <= 'Z'  ⇒ !gt_z
        let le_z = _mm_andnot_si128(gt_z, all_ones);
        let is_upper = _mm_and_si128(ge_a, le_z);
        let folded = _mm_or_si128(chunk, _mm_and_si128(is_upper, lower_bit));
        // SAFETY: store back into the same 16-byte window.
        unsafe { _mm_storeu_si128(ptr.add(offset) as *mut __m128i, folded) };
        offset += 16;
    }

    ascii_lowercase_scalar_tail(bytes, offset);
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2")]
unsafe fn ascii_lowercase_in_place_avx2(bytes: &mut [u8]) {
    use core::arch::x86_64::{
        __m256i, _mm256_and_si256, _mm256_andnot_si256, _mm256_cmpgt_epi8, _mm256_loadu_si256,
        _mm256_or_si256, _mm256_set1_epi8, _mm256_storeu_si256,
    };

    let len = bytes.len();
    let ptr = bytes.as_mut_ptr();
    let mut offset = 0usize;

    let am1 = _mm256_set1_epi8(0x40);
    let z = _mm256_set1_epi8(0x5A);
    let lower_bit = _mm256_set1_epi8(0x20);
    let all_ones = _mm256_set1_epi8(-1i8);

    while offset + 32 <= len {
        // SAFETY: full 32-byte window inside `bytes`.
        let chunk = unsafe { _mm256_loadu_si256(ptr.add(offset) as *const __m256i) };
        let ge_a = _mm256_cmpgt_epi8(chunk, am1);
        let gt_z = _mm256_cmpgt_epi8(chunk, z);
        let le_z = _mm256_andnot_si256(gt_z, all_ones);
        let is_upper = _mm256_and_si256(ge_a, le_z);
        let folded = _mm256_or_si256(chunk, _mm256_and_si256(is_upper, lower_bit));
        // SAFETY: store back into the same 32-byte window.
        unsafe { _mm256_storeu_si256(ptr.add(offset) as *mut __m256i, folded) };
        offset += 32;
    }

    // SAFETY: SSE2 baseline finishes the remainder.
    unsafe { ascii_lowercase_in_place_sse2(&mut bytes[offset..]) };
}

#[cfg(target_arch = "x86_64")]
fn ascii_lowercase_scalar_tail(bytes: &mut [u8], start: usize) {
    for b in &mut bytes[start..] {
        if b.is_ascii_uppercase() {
            *b = b.to_ascii_lowercase();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{
        ascii_lowercase_in_place, case_fold_for_sort, contains_case_insensitive,
        contains_zero_byte, dirs_first_name_key, find_first_of_two,
    };

    #[test]
    fn ascii_case_insensitive_search_matches_across_case_boundaries() {
        assert!(contains_case_insensitive("Alpha BRAVO charlie", "bravo"));
        assert!(contains_case_insensitive("Alpha BRAVO charlie", "ALPHA"));
        assert!(contains_case_insensitive("Alpha BRAVO charlie", "Charlie"));
        assert!(!contains_case_insensitive("Alpha BRAVO charlie", "delta"));
    }

    #[test]
    fn case_insensitive_search_preserves_unicode_lowercase_semantics() {
        let haystack = "\u{0130}stanbul";
        let needle = "i";
        assert_eq!(
            contains_case_insensitive(haystack, needle),
            haystack.to_lowercase().contains(&needle.to_lowercase())
        );
    }

    #[test]
    fn zero_byte_search_finds_only_actual_nul_bytes() {
        assert!(contains_zero_byte(b"abc\0def"));
        assert!(contains_zero_byte(b"\0prefix"));
        assert!(contains_zero_byte(b"suffix\0"));
        assert!(!contains_zero_byte(b"plain text"));
        assert!(!contains_zero_byte(b""));
    }

    #[test]
    fn find_first_of_two_handles_empty_and_edges() {
        assert_eq!(find_first_of_two(b"", b'a', b'A'), None);
        assert_eq!(find_first_of_two(b"zzzz", b'a', b'A'), None);
        assert_eq!(find_first_of_two(b"a", b'a', b'A'), Some(0));
        assert_eq!(find_first_of_two(b"xxxxA", b'a', b'A'), Some(4));
        assert_eq!(find_first_of_two(b"Axxxx", b'a', b'A'), Some(0));
    }

    #[test]
    fn find_first_of_two_crosses_simd_chunk_boundaries() {
        // 15 filler bytes then target at index 15 (end of first SSE lane),
        // and another pattern that lands past 32 for AVX tails.
        let mut buf = vec![b'x'; 15];
        buf.push(b'Q');
        assert_eq!(find_first_of_two(&buf, b'q', b'Q'), Some(15));

        let mut buf32 = vec![b'x'; 31];
        buf32.push(b'Q');
        assert_eq!(find_first_of_two(&buf32, b'q', b'Q'), Some(31));

        let mut buf33 = vec![b'x'; 33];
        buf33.push(b'q');
        assert_eq!(find_first_of_two(&buf33, b'q', b'Q'), Some(33));
    }

    #[test]
    fn find_first_of_two_same_byte_is_single_needle_search() {
        let data = b"............Z............";
        assert_eq!(find_first_of_two(data, b'Z', b'Z'), Some(12));
        assert_eq!(find_first_of_two(data, b'?', b'?'), None);
    }

    #[test]
    fn case_fold_for_sort_matches_to_lowercase_on_ascii() {
        let samples = [
            "",
            "README.md",
            "MiXeD_Case-FILE.TXT",
            "zzzzzzzzzzzzzzzz",                  // 16 bytes
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", // 33 bytes
            "a",
            "Z",
            "123_no_letters",
        ];
        for s in samples {
            assert_eq!(case_fold_for_sort(s), s.to_lowercase(), "ascii fold: {s:?}");
        }
    }

    #[test]
    fn case_fold_for_sort_matches_to_lowercase_on_unicode() {
        let samples = ["İstanbul", "Straße", "naïve", "ΑΒΓ", "日本語", "Café"];
        for s in samples {
            assert_eq!(
                case_fold_for_sort(s),
                s.to_lowercase(),
                "unicode fold: {s:?}"
            );
        }
    }

    #[test]
    fn ascii_lowercase_in_place_only_folds_a_to_z() {
        let mut bytes = b"AbC!@#[\\]^_{|}~\x7f\0".to_vec();
        ascii_lowercase_in_place(&mut bytes);
        assert_eq!(bytes, b"abc!@#[\\]^_{|}~\x7f\0");
    }

    #[test]
    fn dirs_first_name_key_orders_directories_before_files() {
        let dir = dirs_first_name_key(true, "Zoo");
        let file = dirs_first_name_key(false, "alpha");
        assert!(!dir.0); // !is_dir is false for dirs
        assert!(file.0);
        assert_eq!(dir.1, "zoo");
        assert_eq!(file.1, "alpha");
    }

    #[test]
    fn large_buffer_scan_and_fold_round_trip() {
        // Large enough to exercise AVX2 + SSE2 + scalar tails.
        let mut data = vec![b'x'; 10_000];
        data[4095] = b'M';
        data[8191] = b'm';
        data[9999] = b'A';
        assert_eq!(find_first_of_two(&data, b'm', b'M'), Some(4095));

        let mut upper = vec![b'A'; 10_000];
        ascii_lowercase_in_place(&mut upper);
        assert!(upper.iter().all(|&b| b == b'a'));
        assert_eq!(case_fold_for_sort(&"A".repeat(10_000)), "a".repeat(10_000));
    }

    #[test]
    fn empty_needle_matches_any_haystack() {
        assert!(contains_case_insensitive("anything", ""));
        assert!(contains_case_insensitive("", ""));
    }

    #[test]
    fn contains_ascii_finds_match_near_end_of_large_haystack() {
        let mut hay = "x".repeat(5000);
        hay.push_str("TaRgEt");
        hay.push_str(&"y".repeat(100));
        assert!(contains_case_insensitive(&hay, "target"));
        assert!(!contains_case_insensitive(&hay, "missing"));
    }
}
