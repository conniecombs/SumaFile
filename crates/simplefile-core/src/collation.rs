//! Case-insensitive, locale-aware sort keys for file names.
//!
//! The WinUI host orders names with `StringComparer.CurrentCultureIgnoreCase`,
//! which on Windows is ICU collation at secondary strength for the user's
//! culture. A listing view that sorts in the service must put names in the
//! same order, or a folder would reorder itself when its sort moved here:
//! `résumé` sorts beside `resume`, not after `z`.
//!
//! [`Collator::for_locale`] asks the ICU that ships with Windows (`icu.dll`,
//! Windows 10 1903 and later) for sort keys. Two keys compare bytewise the
//! way ICU compares the strings. Where ICU is not available (older Windows,
//! or a non-Windows test build) the collator falls back to the case-folded
//! ordinal order the service already used.

/// Produces sort keys whose bytewise order is the name order.
pub struct Collator {
    imp: Imp,
}

enum Imp {
    #[cfg(windows)]
    Icu(icu::IcuCollator),
    Fold,
}

impl Collator {
    /// A collator for `locale` (a culture name such as `en-US`; `None` or an
    /// empty name is ICU's default locale), or the fallback when ICU is not
    /// available or does not know the locale.
    pub fn for_locale(locale: Option<&str>) -> Self {
        #[cfg(windows)]
        {
            if let Some(collator) = icu::IcuCollator::open(locale.unwrap_or("")) {
                return Self {
                    imp: Imp::Icu(collator),
                };
            }
        }
        let _ = locale;
        Self::fold()
    }

    /// The fallback: lowercase, then ordinal.
    pub fn fold() -> Self {
        Self { imp: Imp::Fold }
    }

    /// True when keys come from ICU rather than the ordinal fallback.
    pub fn is_linguistic(&self) -> bool {
        match self.imp {
            #[cfg(windows)]
            Imp::Icu(_) => true,
            Imp::Fold => false,
        }
    }

    /// The sort key of `text`. Keys from different collators do not compare.
    pub fn sort_key(&self, text: &str) -> Box<[u8]> {
        match &self.imp {
            #[cfg(windows)]
            Imp::Icu(collator) => collator.sort_key(text).unwrap_or_else(|| fold_key(text)),
            Imp::Fold => fold_key(text),
        }
    }
}

fn fold_key(text: &str) -> Box<[u8]> {
    crate::native_accel::case_fold_for_sort(text)
        .into_bytes()
        .into_boxed_slice()
}

#[cfg(windows)]
mod icu {
    use std::ffi::{c_char, c_void, CString};
    use std::sync::OnceLock;
    use windows_sys::Win32::Foundation::HMODULE;
    use windows_sys::Win32::System::LibraryLoader::{
        GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_SYSTEM32,
    };

    /// `UCOL_SECONDARY`: accents distinguish, case does not. .NET maps
    /// `CompareOptions.IgnoreCase` to this strength.
    const UCOL_SECONDARY: i32 = 1;

    type UcolOpen = unsafe extern "C" fn(*const c_char, *mut i32) -> *mut c_void;
    type UcolSetStrength = unsafe extern "C" fn(*mut c_void, i32);
    type UcolGetSortKey = unsafe extern "C" fn(*const c_void, *const u16, i32, *mut u8, i32) -> i32;
    type UcolClose = unsafe extern "C" fn(*mut c_void);

    struct Api {
        open: UcolOpen,
        set_strength: UcolSetStrength,
        get_sort_key: UcolGetSortKey,
        close: UcolClose,
    }

    fn api() -> Option<&'static Api> {
        static API: OnceLock<Option<Api>> = OnceLock::new();
        API.get_or_init(load).as_ref()
    }

    fn load() -> Option<Api> {
        let name: Vec<u16> = "icu.dll".encode_utf16().chain(std::iter::once(0)).collect();
        // SAFETY: `name` is a NUL-terminated UTF-16 string that outlives the
        // call. System32 only: never a DLL beside the executable or on PATH.
        let module = unsafe {
            LoadLibraryExW(
                name.as_ptr(),
                std::ptr::null_mut(),
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        };
        if module.is_null() {
            return None;
        }

        // SAFETY: each symbol is looked up by its documented ICU C name and
        // transmuted to that function's documented C signature. The module
        // is never freed, so the pointers stay valid for the process.
        unsafe {
            Some(Api {
                open: std::mem::transmute::<*const c_void, UcolOpen>(symbol(
                    module,
                    b"ucol_open\0",
                )?),
                set_strength: std::mem::transmute::<*const c_void, UcolSetStrength>(symbol(
                    module,
                    b"ucol_setStrength\0",
                )?),
                get_sort_key: std::mem::transmute::<*const c_void, UcolGetSortKey>(symbol(
                    module,
                    b"ucol_getSortKey\0",
                )?),
                close: std::mem::transmute::<*const c_void, UcolClose>(symbol(
                    module,
                    b"ucol_close\0",
                )?),
            })
        }
    }

    /// # Safety
    /// `name` must be NUL-terminated and `module` a loaded module handle.
    unsafe fn symbol(module: HMODULE, name: &[u8]) -> Option<*const c_void> {
        GetProcAddress(module, name.as_ptr()).map(|address| address as *const c_void)
    }

    pub(super) struct IcuCollator {
        api: &'static Api,
        handle: *mut c_void,
    }

    // SAFETY: after `open` configured it, the collator is only read
    // (`ucol_getSortKey` takes a const collator), which ICU documents as safe
    // from several threads at once. `ucol_close` runs once, in `drop`.
    unsafe impl Send for IcuCollator {}
    unsafe impl Sync for IcuCollator {}

    impl IcuCollator {
        pub(super) fn open(locale: &str) -> Option<Self> {
            let api = api()?;
            // ICU locale ids separate with underscores; culture names with hyphens.
            let locale = CString::new(locale.trim().replace('-', "_")).ok()?;
            let mut status = 0i32;
            // SAFETY: `locale` is NUL-terminated and `status` is a valid
            // out-pointer for the call.
            let handle = unsafe { (api.open)(locale.as_ptr(), &mut status) };
            // A positive UErrorCode is a failure; zero and negatives (such as
            // "fell back to the root locale") are success.
            if handle.is_null() || status > 0 {
                if !handle.is_null() {
                    // SAFETY: `handle` came from `ucol_open` and is closed once.
                    unsafe { (api.close)(handle) };
                }
                return None;
            }
            // SAFETY: `handle` is the open collator.
            unsafe { (api.set_strength)(handle, UCOL_SECONDARY) };
            Some(Self { api, handle })
        }

        pub(super) fn sort_key(&self, text: &str) -> Option<Box<[u8]>> {
            let utf16: Vec<u16> = text.encode_utf16().collect();
            let length = i32::try_from(utf16.len()).ok()?;
            let mut key = vec![0u8; utf16.len() * 3 + 8];
            loop {
                let capacity = i32::try_from(key.len()).ok()?;
                // SAFETY: the source and result pointers are valid for the
                // lengths passed, and the collator is open.
                let needed = unsafe {
                    (self.api.get_sort_key)(
                        self.handle,
                        utf16.as_ptr(),
                        length,
                        key.as_mut_ptr(),
                        capacity,
                    )
                };
                let needed = usize::try_from(needed).ok().filter(|needed| *needed > 0)?;
                if needed <= key.len() {
                    // The count includes the terminating zero byte.
                    key.truncate(needed - 1);
                    return Some(key.into_boxed_slice());
                }
                key.resize(needed, 0);
            }
        }
    }

    impl Drop for IcuCollator {
        fn drop(&mut self) {
            // SAFETY: `handle` came from `ucol_open` and is closed once.
            unsafe { (self.api.close)(self.handle) };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Collator;

    fn sorted(collator: &Collator, names: &[&str]) -> Vec<String> {
        let mut keyed: Vec<(Box<[u8]>, &str)> = names
            .iter()
            .map(|name| (collator.sort_key(name), *name))
            .collect();
        keyed.sort();
        keyed
            .into_iter()
            .map(|(_, name)| name.to_string())
            .collect()
    }

    #[test]
    fn fallback_orders_case_insensitively() {
        let collator = Collator::fold();
        assert!(!collator.is_linguistic());
        assert_eq!(
            vec!["alpha", "Beta", "gamma"],
            sorted(&collator, &["gamma", "Beta", "alpha"])
        );
        assert_eq!(collator.sort_key("README"), collator.sort_key("readme"));
    }

    #[test]
    fn every_collator_ignores_case_and_orders_plain_names() {
        for collator in [
            Collator::for_locale(Some("en-US")),
            Collator::for_locale(None),
        ] {
            assert_eq!(
                collator.sort_key("Report.TXT"),
                collator.sort_key("report.txt")
            );
            assert_eq!(
                vec!["a1", "B2", "c3"],
                sorted(&collator, &["c3", "B2", "a1"])
            );
        }
    }

    #[test]
    fn an_unknown_locale_still_gives_a_working_collator() {
        let collator = Collator::for_locale(Some("zz-not-a-culture"));
        assert_eq!(vec!["a", "b"], sorted(&collator, &["b", "a"]));
    }

    /// The reason ICU is used at all: accented names sort beside their base
    /// letters, as the host's culture comparison does.
    #[test]
    fn the_linguistic_collator_sorts_accents_beside_their_base_letter() {
        let collator = Collator::for_locale(Some("en-US"));
        if !collator.is_linguistic() {
            // No system ICU (a non-Windows build, or Windows before 1903).
            return;
        }
        assert_eq!(
            vec!["resume", "résumé", "zebra"],
            sorted(&collator, &["zebra", "résumé", "resume"])
        );
        // Accents still distinguish: these are different names, in a fixed order.
        assert!(collator.sort_key("resume") < collator.sort_key("résumé"));
    }
}
