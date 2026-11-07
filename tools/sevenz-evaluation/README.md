# sevenz-rust2 evaluation

The 2026-11-07 evaluation **rejected a complete replacement of bundled 7-Zip**.
`sevenz-rust2` 0.25.0 cannot decode Deflate64 inside a `.7z` archive that the
shipping SumaFile adapter successfully lists and extracts. Replacing the engine
would therefore remove existing functionality. The application, its dependency
lockfile, and its release packaging have not been changed.

This isolated Cargo workspace is a repeatable evaluation tool, not a production
archive implementation. Its lockfile pins its own dependencies; it does not
alter the shipping workspace's dependencies. The baseline uses the current
SumaFile archive source with the evaluator's dependency lockfile.

## Reproduce

From the repository root on Windows:

```powershell
cargo run --release --locked --manifest-path tools/sevenz-evaluation/Cargo.toml --target-dir target
```

For just the compatibility checks:

```powershell
cargo run --release --locked --manifest-path tools/sevenz-evaluation/Cargo.toml --target-dir target -- --compatibility-only
```

The tool generates fixtures in a new `test-results/sevenz-evaluation/run-*`
directory. It uses the bundled 26.03 `7za.exe` as the reference, without changing
an installed copy of SumaFile or touching user archives. It writes `results.json`
and returns **exit code 2 when compatibility fails**. Cargo reports this expected
gate failure as a failed command. A setup or unexpected runtime failure exits 1.
An exit code of 0 only means these compatibility checks passed; performance
measurements and full application integration still require review.

Three timing samples per corpus are the default. `SEVENZ_EVAL_ITERATIONS` can
change that count. Benchmarks must run in release mode, with no competing build
or test processes for meaningful timings.

## Observed compatibility

The full results are in [results-2026-11-07.json](results-2026-11-07.json).

| Check | Result |
| --- | --- |
| LZMA2, solid and non-solid | Passed |
| LZMA, Copy, BZip2, PPMd, Deflate | Passed |
| BCJ, BCJ2, Delta, ARM64, RISC-V with LZMA2 | Passed |
| Deflate64 | **Failed** in the candidate; passed in SumaFile |
| Listing names and sizes for all 13 codec/filter cases | Passed |
| Selectively reading a file from each codec/filter case | Passed except Deflate64 |
| Unicode and literal punctuation in names, directories, empty files/directories | Preserved in the successful codec cases |
| Candidate-created solid archive read by both engines | Passed, complete tree and SHA-256 comparison |
| Encrypted content with visible filenames | Both list names and reject extraction without a password |
| Encrypted headers | Both reject listing and extraction without a password |

The Deflate64 error is `UnsupportedCompressionMethod("DEFLATE64")`.
The fixture is produced by the official engine using `a -t7z -m0=Deflate64`.
Both full extraction and the candidate's selective `read_file` fail. Enabling
the candidate's optional `deflate` feature does not add Deflate64 support; that
feature is already enabled in this evaluation.

This is not a claim of exhaustive compatibility. Corruption handling, all
timestamps/attributes, links, collision handling, resource limits, multi-volume
archives, previews, and every mutation path would need a separate application
integration test pass before removing the reference engine. A demonstrated
codec regression already fails the user's requirement to lose no functionality.

## Initial performance measurements

Machine: AMD Ryzen 9 9950X, 16 cores / 32 logical processors, Windows x64,
approximately 93.6 GiB RAM. Rust 1.98.0, optimized release build. Values below
are medians of three runs. Every extracted tree and every candidate-created
archive decoded by 7-Zip was checked against source file sizes and SHA-256 hashes.

| Corpus | 7za create | Rust create | 7za extract | Rust extract |
| --- | ---: | ---: | ---: | ---: |
| 1,000 small text files, 4.57 MiB | 62 ms | 361 ms | 149 ms | 137 ms |
| Structured text, 32 MiB | 5.41 s | 14.01 s | 91 ms | 128 ms |
| Deterministic incompressible data, 32 MiB | 1.40 s | 10.57 s | 41 ms | 49 ms |

Listing was faster in the candidate (roughly 0.1–0.5 ms versus 12 ms).
Candidate output was about 11% smaller for the small-text corpus, 6% smaller for
the large-text corpus, and effectively the same size for incompressible data.

These are **initial configuration comparisons, not maximum-performance claims**:
7za uses SumaFile's existing default compression switches; the candidate uses
LZMA2 preset 6, up to eight compression workers, and 32 MiB independent chunks.
These corpora do not provide enough chunks to use all eight workers. Smaller
chunks, different presets, and larger corpora could change the speed/size
tradeoff. No further tuning was used to justify a migration after the codec
compatibility gate failed. Extraction times decode the same 7za-created archive.
Creation/extraction measurements compare the engines, not the complete proposed
SumaFile integration. The candidate's stock extraction helper is used only on
generated fixtures here and is not a replacement for SumaFile's extraction
validation, staging, collision, and resource-limit policies.

The full evaluation completed with the expected compatibility-gate failure;
the subsequent compatibility-only run reproduced it. The evaluator passes
`cargo fmt --check` and release-mode `cargo clippy -- -D warnings`.

## Source-engine alternative

Building upstream 7-Zip source into an application-private DLL is the next
candidate for preserving codec support while removing the console executable.
Upstream has a `CPP/7zip/Bundles/Format7zF` target for `7z.dll` and a
`CPP/7zip/UI/Client7z` example showing list/create/extract interfaces. A narrow
C-compatible bridge can hide the native interfaces from Rust. The official
codec implementations should minimize compatibility and compression-performance
differences, but the resulting adapter still needs the full behavioral checks.

Keep an unmodified, pinned upstream source release and a small separate bridge;
do not scatter copied codecs throughout the Rust code. Source updates, native
build tooling, license notices/source obligations, and packaged DLL loading
become part of SumaFile's build/release process. No such integration has been
implemented by this evaluation.

Sources:

- [Candidate documentation](https://docs.rs/sevenz-rust2/0.25.0/sevenz_rust2/)
- [Official engine build target](https://github.com/ip7z/7zip/blob/main/CPP/7zip/Bundles/Format7zF/makefile)
- [Official client example](https://github.com/ip7z/7zip/blob/main/CPP/7zip/UI/Client7z/Client7z.cpp)
- [7-Zip developer FAQ and licensing guidance](https://www.7-zip.org/faq.html#developer_faq)
