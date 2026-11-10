//! Path spellings: native paths for the UI, `/`-separated repository-relative
//! pathspecs for git, and comparison keys.

use std::path::{Component, Path, PathBuf, MAIN_SEPARATOR_STR};

/// `text` with every `/` turned into the native separator, as Git for
/// Windows reports `C:/repo` for `C:\repo`. A no-op where `/` is native.
pub fn native_path(text: &str) -> PathBuf {
    PathBuf::from(text.replace('/', MAIN_SEPARATOR_STR))
}

/// `path` when it exists, without resolving it.
pub fn existing_path(path: &str) -> Result<PathBuf, String> {
    let path_buf = PathBuf::from(path);
    std::fs::metadata(&path_buf)
        .map_err(|error| format!("Path does not exist or is not accessible: {path} ({error})"))?;
    Ok(path_buf)
}

/// The sorted, deduplicated pathspecs of `paths` relative to `root`. Fails
/// on an empty selection and on any path that names the root itself, once
/// `.` and `..` are resolved (`sub\..` is the root), or that leaves it.
pub fn pathspecs_under_root(root: &Path, paths: &[String]) -> Result<Vec<String>, String> {
    if paths.is_empty() {
        return Err("Select at least one Git path.".to_string());
    }

    let mut pathspecs = Vec::with_capacity(paths.len());
    for path in paths {
        let pathspec = repo_relative_path(root, path)?;
        if pathspec.is_empty() || pathspec == "." {
            return Err(
                "Refusing to run a file-level Git action against the repository root.".to_string(),
            );
        }

        pathspecs.push(pathspec);
    }

    pathspecs.sort_unstable();
    pathspecs.dedup();
    Ok(pathspecs)
}

/// `value`, absolute or relative to `root`, as a `/`-separated path relative
/// to `root`, with `.` and `..` resolved lexically first, as git resolves
/// them in a pathspec: `sub/..` is `""` (the root) and `sub/../..` is
/// outside. A root spelled with other ASCII letter cases still matches, and
/// the rest of the path keeps its own spelling.
pub fn repo_relative_path(root: &Path, value: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        return Err("Git path cannot be empty.".to_string());
    }

    let input = Path::new(value);
    let root = &lexically_normal(root);
    let absolute = lexically_normal(&if input.is_absolute() {
        input.to_path_buf()
    } else {
        root.join(input)
    });

    if let Ok(relative) = absolute.strip_prefix(root) {
        return Ok(path_to_git_relative(relative));
    }

    let root_text = slash_path(root);
    let absolute_text = slash_path(&absolute);
    let root_length = root_text.len();
    if absolute_text
        .get(..root_length)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case(&root_text))
        && absolute_text[root_length..].starts_with('/')
    {
        return Ok(absolute_text[root_length + 1..].to_string());
    }

    // Other case differences, as 1.0.4 compared them: the remainder comes
    // back lowercased.
    let root_lower = root_text.to_lowercase();
    let absolute_lower = absolute_text.to_lowercase();
    let prefix = if root_lower.ends_with('/') {
        root_lower
    } else {
        format!("{root_lower}/")
    };
    if !absolute_lower.starts_with(&prefix) {
        return Err(format!(
            "Git path is outside the repository: {}",
            absolute.to_string_lossy()
        ));
    }

    Ok(absolute_lower[prefix.len()..].to_string())
}

/// `path` without `.` components, each `..` removing the name before it. A
/// `..` at the top of an absolute path stays there, as the file system keeps
/// it; one at the start of a relative path is kept.
fn lexically_normal(path: &Path) -> PathBuf {
    let mut normal = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => match normal.components().next_back() {
                Some(Component::Normal(_)) => {
                    normal.pop();
                }
                Some(Component::RootDir | Component::Prefix(_)) => {}
                _ => normal.push(".."),
            },
            other => normal.push(other.as_os_str()),
        }
    }
    normal
}

pub fn path_to_git_relative(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

/// `path` with `/` separators and no trailing one.
fn slash_path(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "/")
        .trim_end_matches('/')
        .to_string()
}

/// A comparison key for a local path: `/` separators, no trailing one, and
/// lowercase on Windows, whose file systems ignore case.
pub fn comparable_path(path: &Path) -> String {
    let text = slash_path(path);
    if cfg!(windows) {
        text.to_lowercase()
    } else {
        text
    }
}

/// Two repository-relative paths name the same entry.
pub fn paths_equal(left: &str, right: &str) -> bool {
    left.replace('\\', "/")
        .trim_end_matches('/')
        .eq_ignore_ascii_case(right.replace('\\', "/").trim_end_matches('/'))
}

#[cfg(test)]
mod tests {
    use super::{pathspecs_under_root, repo_relative_path};
    use std::path::Path;

    #[cfg(windows)]
    const ROOT: &str = "C:/repo";
    #[cfg(windows)]
    const OTHER: &str = "C:/other/lib.rs";
    #[cfg(not(windows))]
    const ROOT: &str = "/repo";
    #[cfg(not(windows))]
    const OTHER: &str = "/other/lib.rs";

    #[test]
    fn repo_relative_path_rejects_paths_outside_root() {
        let root = Path::new(ROOT);

        assert_eq!(
            repo_relative_path(root, "src/lib.rs").unwrap(),
            "src/lib.rs"
        );
        assert!(repo_relative_path(root, OTHER).is_err());
    }

    #[test]
    fn repo_relative_path_keeps_the_spelling_after_a_case_variant_root() {
        let root = Path::new(ROOT);
        let upper = format!("{}/Src/Lib.rs", ROOT.to_uppercase());

        assert_eq!(repo_relative_path(root, &upper).unwrap(), "Src/Lib.rs");
    }

    #[test]
    fn pathspecs_refuse_the_root_and_an_empty_selection() {
        let root = Path::new(ROOT);

        assert_eq!(
            pathspecs_under_root(root, &[ROOT.to_string()]).unwrap_err(),
            "Refusing to run a file-level Git action against the repository root."
        );
        assert_eq!(
            pathspecs_under_root(root, &[]).unwrap_err(),
            "Select at least one Git path."
        );
        assert_eq!(
            pathspecs_under_root(root, &["b".to_string(), "a".to_string(), "b".to_string()])
                .unwrap(),
            vec!["a", "b"]
        );
    }

    #[test]
    fn dot_dot_that_lands_on_the_root_is_the_root() {
        let root = Path::new(ROOT);
        let refused = "Refusing to run a file-level Git action against the repository root.";

        for path in [
            "sub/..".to_string(),
            "./sub/../.".to_string(),
            format!("{ROOT}/sub/.."),
            format!("{ROOT}/sub/deeper/../.."),
        ] {
            assert_eq!(
                pathspecs_under_root(root, std::slice::from_ref(&path)).unwrap_err(),
                refused,
                "{path}"
            );
        }
    }

    #[test]
    fn dot_dot_that_leaves_the_root_is_outside_the_repository() {
        let root = Path::new(ROOT);

        for path in ["sub/../..", "../other/lib.rs"] {
            let error = pathspecs_under_root(root, &[path.to_string()]).unwrap_err();
            assert!(
                error.starts_with("Git path is outside the repository: "),
                "{path}: {error}"
            );
        }
        let repo_lookalike = format!("{ROOT}/../repo2/a.txt");
        assert!(repo_relative_path(root, &repo_lookalike).is_err());
    }

    #[test]
    fn dot_and_dot_dot_inside_the_root_resolve_lexically() {
        let root = Path::new(ROOT);

        assert_eq!(
            repo_relative_path(root, "sub/./a/../b.txt").unwrap(),
            "sub/b.txt"
        );
        assert_eq!(repo_relative_path(root, "./a.txt").unwrap(), "a.txt");
        assert_eq!(
            repo_relative_path(root, &format!("{ROOT}/sub/../a.txt")).unwrap(),
            "a.txt"
        );
    }
}
