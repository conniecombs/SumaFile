## Description

<!-- A clear, concise description of what this PR does. -->

Closes #<!-- issue number -->

---

## Type of Change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Performance improvement (non-breaking change that improves speed or memory use)
- [ ] Breaking change (fix or feature that changes existing behavior)
- [ ] Refactor (no behavior change)
- [ ] Documentation / comments only

---

## How Has This Been Tested?

<!-- SumaFile is Windows-only. Describe the Windows environment and exact test coverage. -->

**Windows version:**
- [ ] Windows 11
- [ ] Windows 10
- [ ] Other Windows version:

**Windows build tools:**
- [ ] .NET SDK 8 and stable Rust are available
- [ ] Not applicable for this change

**Run/install mode tested:**
- [ ] `npm run dev:winui`
- [ ] Unpackaged payload (`dist/winui/payload/SumaFile.exe`)
- [ ] NSIS installer
- [ ] MSI installer
- [ ] Not applicable for this change

**Storage context tested:**
- [ ] Local drive
- [ ] Mapped network drive or UNC path
- [ ] Removable drive
- [ ] Not applicable for this change

**Test steps:**
1.
2.

---

## Checklist

- [ ] I have run `cargo fmt --all` and there are no formatting issues
- [ ] I have run `cargo clippy --locked --all-targets --all-features -- -D warnings` with no new warnings
- [ ] I have run `cargo test --locked --all-features` and all tests pass
- [ ] I have run `npm run check`
- [ ] I have run `npm run check:release` for release-level or migration changes
- [ ] I have run the documented `cargo audit --deny warnings` command and there are no new advisories
- [ ] I have tested on Windows or explained why runtime testing is not applicable
- [ ] New IPC methods validate path inputs via `validate_existing_path()` or `validate_name()`
- [ ] No user-controlled data is passed to shell commands without sanitization
- [ ] Drive-listing changes preserve local and mapped network drive behavior
- [ ] I have added tests for new backend behavior (or explained why tests are not applicable)
- [ ] I have updated documentation if the change affects user-facing behavior

---

## Screenshots (if UI change)

<!-- Before / after screenshots or a GIF are very helpful for UI changes. -->
