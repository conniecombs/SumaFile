//! The Attributes column: Windows file attribute flags as the letters
//! Explorer shows.

/// Explorer's letters for `attrs` (a `dwFileAttributes` value), in Explorer's
/// order: R read-only, H hidden, S system, A archive, C compressed,
/// E encrypted, O offline, T temporary, then L for a reparse point (a
/// symlink, junction or cloud placeholder). Directory and Normal carry none.
pub fn attribute_letters(attrs: u32) -> String {
    const LETTERS: [(u32, char); 9] = [
        (0x0001, 'R'), // FILE_ATTRIBUTE_READONLY
        (0x0002, 'H'), // FILE_ATTRIBUTE_HIDDEN
        (0x0004, 'S'), // FILE_ATTRIBUTE_SYSTEM
        (0x0020, 'A'), // FILE_ATTRIBUTE_ARCHIVE
        (0x0800, 'C'), // FILE_ATTRIBUTE_COMPRESSED
        (0x4000, 'E'), // FILE_ATTRIBUTE_ENCRYPTED
        (0x1000, 'O'), // FILE_ATTRIBUTE_OFFLINE
        (0x0100, 'T'), // FILE_ATTRIBUTE_TEMPORARY
        (0x0400, 'L'), // FILE_ATTRIBUTE_REPARSE_POINT
    ];
    LETTERS
        .iter()
        .filter(|(flag, _)| attrs & flag != 0)
        .map(|(_, letter)| *letter)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::attribute_letters;

    #[test]
    fn attribute_letters_follow_explorers_order() {
        // READONLY | HIDDEN | ARCHIVE
        assert_eq!("RHA", attribute_letters(0x0001 | 0x0002 | 0x0020));
        // REPARSE_POINT | OFFLINE
        assert_eq!("OL", attribute_letters(0x0400 | 0x1000));
        // SYSTEM | COMPRESSED | ENCRYPTED | TEMPORARY
        assert_eq!("SCET", attribute_letters(0x0004 | 0x0800 | 0x4000 | 0x0100));
        assert_eq!("", attribute_letters(0));
        // DIRECTORY and NORMAL carry no letter.
        assert_eq!("", attribute_letters(0x0010 | 0x0080));
    }
}
