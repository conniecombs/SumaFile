//! Reproducible release-mode compatibility and performance gate. Uses only
//! generated fixtures in a fresh output directory; never modifies user archives.
mod plugin_engine;

use std::{
    collections::BTreeMap,
    fs::{self, File},
    io::{self, BufReader, BufWriter, Read, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    time::{Instant, SystemTime, UNIX_EPOCH},
};

use serde_json::{json, Value};
use sevenz_rust2::{
    encoder_options::Lzma2Options, ArchiveEntry, ArchiveReader, ArchiveWriter, Password,
};
use sha2::{Digest, Sha256};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
type Snapshot = BTreeMap<String, (u64, String)>;

fn run_7z(binary: &Path, args: &[String]) -> Result<String> {
    let result = Command::new(binary)
        .args(args)
        .stdin(Stdio::null())
        .output()?;
    if !result.status.success() {
        return Err(format!(
            "7za {:?}: {} {}",
            args,
            String::from_utf8_lossy(&result.stdout),
            String::from_utf8_lossy(&result.stderr)
        )
        .into());
    }
    Ok(String::from_utf8_lossy(&result.stdout).into_owned())
}

fn cli_create(binary: &Path, source: &Path, archive: &Path, options: &[&str]) -> Result<()> {
    let mut args: Vec<String> = [
        "a",
        "-t7z",
        "-bd",
        "-bb0",
        "-sccUTF-8",
        "-y",
        "-sse",
        "-spd",
    ]
    .into_iter()
    .map(String::from)
    .collect();
    args.extend(options.iter().map(|s| s.to_string()));
    args.extend([
        "--".into(),
        archive.display().to_string(),
        source.display().to_string(),
    ]);
    run_7z(binary, &args)?;
    Ok(())
}

fn cli_extract(binary: &Path, archive: &Path, out: &Path) -> Result<()> {
    run_7z(
        binary,
        &[
            "x".into(),
            format!("-o{}", out.display()),
            "-bd".into(),
            "-bb0".into(),
            "-sccUTF-8".into(),
            "-y".into(),
            "-spd".into(),
            "-aoa".into(),
            "--".into(),
            archive.display().to_string(),
        ],
    )?;
    Ok(())
}

fn files_under(root: &Path) -> Result<Vec<PathBuf>> {
    fn visit(dir: &Path, paths: &mut Vec<PathBuf>) -> Result<()> {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            let ty = entry.file_type()?;
            if ty.is_symlink() {
                return Err("Fixture must not contain symlinks".into());
            }
            paths.push(entry.path());
            if ty.is_dir() {
                visit(&entry.path(), paths)?;
            }
        }
        Ok(())
    }
    let mut paths = vec![root.to_path_buf()];
    visit(root, &mut paths)?;
    paths.sort();
    Ok(paths)
}

fn snapshot(root: &Path) -> Result<Snapshot> {
    let mut result = Snapshot::new();
    for path in files_under(root)?.into_iter().skip(1) {
        let name = path
            .strip_prefix(root)?
            .to_string_lossy()
            .replace('\\', "/");
        if path.is_dir() {
            result.insert(format!("{name}/"), (0, "directory".into()));
        } else {
            let mut file = BufReader::new(File::open(&path)?);
            let mut hasher = Sha256::new();
            let mut buf = [0; 64 * 1024];
            loop {
                let n = file.read(&mut buf)?;
                if n == 0 {
                    break;
                }
                hasher.update(&buf[..n]);
            }
            result.insert(
                name,
                (
                    path.metadata()?.len(),
                    hasher
                        .finalize()
                        .iter()
                        .map(|byte| format!("{byte:02x}"))
                        .collect(),
                ),
            );
        }
    }
    Ok(result)
}

// Open one source at a time, rather than holding thousands of file handles.
struct LazyFile {
    path: PathBuf,
    file: Option<BufReader<File>>,
}
impl Read for LazyFile {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.file.is_none() {
            self.file = Some(BufReader::new(File::open(&self.path)?));
        }
        self.file.as_mut().unwrap().read(buf)
    }
}

fn rust_create(source: &Path, archive: &Path, threads: u32) -> Result<()> {
    let output = File::options().write(true).create_new(true).open(archive)?;
    let mut writer = ArchiveWriter::new(BufWriter::new(output))?;
    // 7za defaults to normal LZMA2 compression. Test a normal preset with
    // explicit parallelism, rather than penalizing the crate's single-thread default.
    let options = Lzma2Options::from_level_mt(6, threads, 32 * 1024 * 1024);
    writer.set_content_methods(vec![options.into()]);
    let parent = source.parent().ok_or("Fixture has no parent")?;
    let mut entries = Vec::new();
    let mut readers = Vec::new();
    for path in files_under(source)? {
        let name = path
            .strip_prefix(parent)?
            .to_string_lossy()
            .replace('\\', "/");
        let mut entry = ArchiveEntry::from_path(&path, name);
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            entry.has_windows_attributes = true;
            entry.windows_attributes = path.metadata()?.file_attributes();
        }
        if path.is_dir() || path.metadata()?.len() == 0 {
            entry.has_stream = false;
            writer.push_archive_entry::<File>(entry, None)?;
        } else {
            entries.push(entry);
            readers.push(LazyFile { path, file: None }.into());
        }
    }
    if !entries.is_empty() {
        writer.push_archive_entries(entries, readers)?;
    }
    writer.finish()?.flush()?;
    Ok(())
}

fn verify_extract(archive: &Path, out: &Path, expected: &Snapshot) -> Result<()> {
    sevenz_rust2::decompress_file(archive, out)?;
    if snapshot(&out.join("source"))? != *expected {
        return Err("Extracted tree/hash mismatch".into());
    }
    Ok(())
}

fn status(result: Result<()>) -> Value {
    match result {
        Ok(()) => json!({"passed": true}),
        Err(e) => json!({"passed": false, "error": e.to_string()}),
    }
}

fn write_report(path: &Path, report: &Value) -> Result<()> {
    fs::write(path, serde_json::to_string_pretty(report)?)?;
    Ok(())
}

fn compatibility(binary: &Path, root: &Path) -> Result<Value> {
    let source = root.join("source");
    fs::create_dir_all(source.join("café 資料/empty directory"))?;
    fs::write(source.join("zero.txt"), [])?;
    fs::write(
        source.join("café 資料/hello.txt"),
        "Unicode paths, nested folders, and content.\n".repeat(1000),
    )?;
    fs::copy(binary, source.join("program.exe"))?;
    fs::write(
        source.join("literal [brackets] & spaces.txt"),
        b"literal filename",
    )?;
    let expected = snapshot(&source)?;
    let cases: &[(&str, &[&str])] = &[
        ("lzma2-solid", &["-m0=LZMA2"]),
        ("lzma2-non-solid", &["-m0=LZMA2", "-ms=off"]),
        ("lzma", &["-m0=LZMA"]),
        ("copy", &["-m0=Copy"]),
        ("bzip2", &["-m0=BZip2"]),
        ("ppmd", &["-m0=PPMd"]),
        ("deflate", &["-m0=Deflate"]),
        ("deflate64", &["-m0=Deflate64"]),
        ("bcj-lzma2", &["-m0=BCJ", "-m1=LZMA2"]),
        (
            "bcj2-lzma2",
            &[
                "-m0=BCJ2",
                "-m1=LZMA2",
                "-m2=LZMA2",
                "-m3=LZMA2",
                "-mb0:1",
                "-mb0s1:2",
                "-mb0s2:3",
            ],
        ),
        ("delta-lzma2", &["-m0=Delta:4", "-m1=LZMA2"]),
        ("arm64-lzma2", &["-m0=ARM64", "-m1=LZMA2"]),
        ("riscv-lzma2", &["-m0=RISCV", "-m1=LZMA2"]),
    ];
    let mut results = Vec::new();
    for (label, options) in cases {
        let archive = root.join(format!("{label}.7z"));
        cli_create(binary, &source, &archive, options)?;
        // Exercise the actual shipping adapter, not just 7za, on every fixture.
        let baseline = status((|| {
            let listing = simplefile_core::archive::list_archive(archive.display().to_string())?;
            if !listing.unsafe_entries.is_empty() {
                return Err("Unexpected unsafe fixture entries".into());
            }
            let out = root.join(format!("{label}-sumafile"));
            simplefile_core::archive::extract_archive(
                archive.display().to_string(),
                out.display().to_string(),
            )?;
            if snapshot(&out.join("source"))? != expected {
                return Err("Baseline content mismatch".into());
            }
            Ok(())
        })());
        let rust_listing = status((|| {
            let reader = ArchiveReader::open(&archive, Password::empty())?;
            let files: BTreeMap<_, _> = reader
                .archive()
                .files
                .iter()
                .filter(|e| !e.is_directory)
                .map(|e| (e.name.replace('\\', "/"), e.size))
                .collect();
            let wanted: BTreeMap<_, _> = expected
                .iter()
                .filter(|(n, _)| !n.ends_with('/'))
                .map(|(n, (s, _))| (format!("source/{n}"), *s))
                .collect();
            if files != wanted {
                return Err("Listing names/sizes differ".into());
            }
            Ok(())
        })());
        let rust_extract = status(verify_extract(
            &archive,
            &root.join(format!("{label}-rust")),
            &expected,
        ));
        let selective_read = status((|| {
            let mut reader = ArchiveReader::open(&archive, Password::empty())?;
            let data = reader.read_file("source/café 資料/hello.txt")?;
            if data != fs::read(source.join("café 資料/hello.txt"))? {
                return Err("Selected content mismatch".into());
            }
            Ok(())
        })());
        println!(
            "Compatibility {label}: baseline={} candidate={}",
            baseline["passed"], rust_extract["passed"]
        );
        results.push(
            json!({"case": label, "sumafile": baseline, "rust_listing": rust_listing,
            "rust_extract": rust_extract, "rust_selective_read": selective_read}),
        );
    }
    let created = root.join("rust-created.7z");
    rust_create(&source, &created, 2)?;
    let roundtrip = status((|| {
        cli_extract(binary, &created, &root.join("rust-to-cli"))?;
        if snapshot(&root.join("rust-to-cli/source"))? != expected {
            return Err("7za reading Rust output differs".into());
        }
        verify_extract(&created, &root.join("rust-to-rust"), &expected)
    })());
    // SumaFile currently has no password parameter. Confirm both engines reject
    // encrypted content, and distinguish readable names from encrypted headers.
    let mut passwords = Vec::new();
    for encrypted_header in [false, true] {
        let label = if encrypted_header {
            "encrypted-header"
        } else {
            "encrypted-content"
        };
        let archive = root.join(format!("{label}.7z"));
        cli_create(
            binary,
            &source,
            &archive,
            &[
                "-pFixturePassword",
                if encrypted_header {
                    "-mhe=on"
                } else {
                    "-mhe=off"
                },
            ],
        )?;
        passwords.push(json!({"case": label,
            "sumafile_lists_without_password": simplefile_core::archive::list_archive(archive.display().to_string()).is_ok(),
            "rust_lists_without_password": ArchiveReader::open(&archive, Password::empty()).is_ok(),
            "sumafile_rejects_extract_without_password": simplefile_core::archive::extract_archive(
                archive.display().to_string(), root.join(format!("{label}-sumafile")).display().to_string()).is_err(),
            "rust_rejects_extract_without_password": sevenz_rust2::decompress_file(
                &archive, root.join(format!("{label}-rust"))).is_err(),
        }));
    }
    Ok(
        json!({"codecs": results, "rust_creation_cross_read": roundtrip, "password_behavior": passwords}),
    )
}

fn make_corpus(source: &Path, kind: &str) -> Result<()> {
    fs::create_dir_all(source)?;
    match kind {
        "many-small" => {
            for i in 0..1000 {
                let dir = source.join(format!("folder-{:02}", i % 20));
                fs::create_dir_all(&dir)?;
                fs::write(
                    dir.join(format!("file-{i:04}.txt")),
                    format!("record {i}: path metadata and transfer progress\n").repeat(100),
                )?;
            }
        }
        "text-32m" => {
            let mut out = BufWriter::new(File::create(source.join("records.txt"))?);
            let mut written = 0;
            for i in 0u64.. {
                let line = format!("{{\"id\":{i},\"path\":\"photos/{}/image-{}.jpg\",\"bytes\":{},\"status\":\"completed\"}}\n", i % 7919, i % 997, i.wrapping_mul(10007) % 10000000);
                out.write_all(line.as_bytes())?;
                written += line.len();
                if written >= 32 * 1024 * 1024 {
                    break;
                }
            }
            out.flush()?;
        }
        "random-32m" => {
            let mut out = BufWriter::new(File::create(source.join("incompressible.bin"))?);
            let mut state = 0x7157_75af_951a_29d3u64;
            let mut block = [0; 65536];
            for _ in 0..512 {
                for word in block.as_chunks_mut::<8>().0 {
                    state ^= state << 13;
                    state ^= state >> 7;
                    state ^= state << 17;
                    word.copy_from_slice(&state.to_le_bytes());
                }
                out.write_all(&block)?;
            }
            out.flush()?;
        }
        _ => return Err("Unknown corpus".into()),
    }
    Ok(())
}

fn timed(f: impl FnOnce() -> Result<()>) -> Result<f64> {
    let start = Instant::now();
    f()?;
    Ok(start.elapsed().as_secs_f64() * 1000.)
}

fn benchmark(binary: &Path, root: &Path, iterations: usize, threads: u32) -> Result<Value> {
    let mut all = Vec::new();
    for kind in ["many-small", "text-32m", "random-32m"] {
        let corpus = root.join(kind);
        let source = corpus.join("source");
        make_corpus(&source, kind)?;
        let expected = snapshot(&source)?;
        let mut samples = Vec::new();
        for i in 0..iterations {
            let cli = corpus.join(format!("cli-{i}.7z"));
            let rust = corpus.join(format!("rust-{i}.7z"));
            // Alternate order to reduce consistent warm-cache/CPU-order bias.
            let (cli_create_ms, rust_create_ms) = if i % 2 == 0 {
                let c = timed(|| cli_create(binary, &source, &cli, &[]))?;
                let r = timed(|| rust_create(&source, &rust, threads))?;
                (c, r)
            } else {
                let r = timed(|| rust_create(&source, &rust, threads))?;
                let c = timed(|| cli_create(binary, &source, &cli, &[]))?;
                (c, r)
            };
            let cli_out = corpus.join(format!("cli-out-{i}"));
            let rust_out = corpus.join(format!("rust-out-{i}"));
            let (cli_extract_ms, rust_extract_ms) = if i % 2 == 0 {
                let c = timed(|| cli_extract(binary, &cli, &cli_out))?;
                let r = timed(|| {
                    sevenz_rust2::decompress_file(&cli, &rust_out)?;
                    Ok(())
                })?;
                (c, r)
            } else {
                let r = timed(|| {
                    sevenz_rust2::decompress_file(&cli, &rust_out)?;
                    Ok(())
                })?;
                let c = timed(|| cli_extract(binary, &cli, &cli_out))?;
                (c, r)
            };
            if snapshot(&cli_out.join("source"))? != expected
                || snapshot(&rust_out.join("source"))? != expected
            {
                return Err("Benchmark extraction hash mismatch".into());
            }
            let cross_out = corpus.join(format!("cross-out-{i}"));
            cli_extract(binary, &rust, &cross_out)?;
            if snapshot(&cross_out.join("source"))? != expected {
                return Err("Benchmark cross-read mismatch".into());
            }
            let cli_list_ms = timed(|| {
                run_7z(
                    binary,
                    &[
                        "l".into(),
                        "-slt".into(),
                        "-bd".into(),
                        "--".into(),
                        cli.display().to_string(),
                    ],
                )?;
                Ok(())
            })?;
            let rust_list_ms = timed(|| {
                ArchiveReader::open(&cli, Password::empty())?;
                Ok(())
            })?;
            let row = json!({"iteration": i + 1, "cli_create_ms": cli_create_ms, "rust_create_ms": rust_create_ms,
                "cli_extract_ms": cli_extract_ms, "rust_extract_ms": rust_extract_ms, "cli_list_ms": cli_list_ms,
                "rust_list_ms": rust_list_ms, "cli_bytes": cli.metadata()?.len(), "rust_bytes": rust.metadata()?.len()});
            println!("Benchmark {kind} {row}");
            samples.push(row);
        }
        all.push(json!({"corpus": kind, "input_bytes": expected.values().map(|(s,_)| s).sum::<u64>(),
            "file_count": expected.keys().filter(|n| !n.ends_with('/')).count(), "samples": samples}));
    }
    Ok(json!(all))
}

fn main() -> Result<()> {
    let repo = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .canonicalize()?;
    // The reference is the SumaFile 7-Zip plugin with its bundled 7za: the
    // binary resolves the way the plugin resolves it (no override, then the
    // bundled copy, which staging takes from third_party), and the plugin's
    // library is registered with the core as the `.7z` engine, so the
    // baseline still goes through core listing, validation and staging.
    let binary = sumafile_sevenzip_backend::binary::resolve_binary(
        None,
        &repo.join("third_party/7zip/win-x64"),
    )?;
    simplefile_core::archive::register_engine(
        sumafile_sevenzip_backend::PLUGIN_ID,
        vec![simplefile_core::archive::EngineFormat {
            extension: ".7z".to_string(),
            can_list: true,
            can_extract: true,
            can_create: true,
            can_modify: true,
        }],
        std::sync::Arc::new(plugin_engine::PluginEngine::new(binary.clone())),
    );
    let nonce = SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis();
    let root = repo.join(format!("test-results/sevenz-evaluation/run-{nonce}"));
    fs::create_dir_all(&root)?;
    println!("Evaluation output: {}", root.display());
    let iterations = std::env::var("SEVENZ_EVAL_ITERATIONS")
        .ok()
        .and_then(|s| s.parse().ok())
        .unwrap_or(3);
    if iterations == 0 {
        return Err("SEVENZ_EVAL_ITERATIONS must be positive".into());
    }
    let compatibility_only = std::env::args().any(|arg| arg == "--compatibility-only");
    let threads = std::thread::available_parallelism()
        .map_or(1, |n| n.get() as u32)
        .min(8);
    let mut report = json!({"candidate_version": "0.25.0", "reference": binary,
        "reference_version": run_7z(&binary, &[])?.lines().find(|s| s.starts_with("7-Zip")).unwrap_or("unknown"),
        "release_mode": !cfg!(debug_assertions), "logical_processors": std::thread::available_parallelism()?.get(),
        "candidate_compression_threads": threads, "candidate_preset": 6, "candidate_chunk_bytes": 33554432,
        "iterations": iterations, "scope": "Generated fixtures; engine benchmarks, not an integrated replacement"});
    let output = root.join("results.json");
    report["compatibility"] = compatibility(&binary, &root.join("compatibility"))?;
    write_report(&output, &report)?;
    if !compatibility_only {
        report["benchmarks"] = benchmark(&binary, &root.join("benchmarks"), iterations, threads)?;
    }
    report["compatibility_passed"] = json!(
        report["compatibility"]["codecs"]
            .as_array()
            .unwrap()
            .iter()
            .all(|v| v["sumafile"]["passed"] == true
                && v["rust_listing"]["passed"] == true
                && v["rust_extract"]["passed"] == true
                && v["rust_selective_read"]["passed"] == true)
            && report["compatibility"]["rust_creation_cross_read"]["passed"] == true
            && report["compatibility"]["password_behavior"]
                .as_array()
                .unwrap()
                .iter()
                .all(
                    |v| v["sumafile_lists_without_password"] == v["rust_lists_without_password"]
                        && v["sumafile_rejects_extract_without_password"] == true
                        && v["rust_rejects_extract_without_password"] == true
                )
    );
    write_report(&output, &report)?;
    println!("Results: {}", output.display());
    // A completed evaluation with failed compatibility is a gate failure, not success.
    if report["compatibility_passed"] != true {
        std::process::exit(2);
    }
    Ok(())
}
