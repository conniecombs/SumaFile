use chrono::{DateTime, SecondsFormat, Utc};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub(crate) fn normalize_remote_path(path: &str) -> String {
    let replaced = path.trim().replace('\\', "/");
    let parts: Vec<&str> = replaced
        .split('/')
        .filter(|part| !part.is_empty() && *part != ".")
        .collect();

    if parts.is_empty() {
        "/".to_string()
    } else {
        format!("/{}", parts.join("/"))
    }
}

pub(crate) fn join_remote_path(parent: &str, child: &str) -> String {
    let child = child.trim().trim_matches('/').trim_matches('\\');
    if child.is_empty() {
        return normalize_remote_path(parent);
    }

    let parent = normalize_remote_path(parent);
    if parent == "/" {
        format!("/{child}")
    } else {
        format!("{parent}/{child}")
    }
}

pub(crate) fn sibling_remote_path(path: &str, new_name: &str) -> String {
    match parent_remote_path(path) {
        Some(parent) => join_remote_path(&parent, new_name),
        None => join_remote_path("/", new_name),
    }
}

pub(crate) fn parent_remote_path(path: &str) -> Option<String> {
    let normalized = normalize_remote_path(path);
    if normalized == "/" {
        return None;
    }

    let parent = normalized.rsplit_once('/').map(|(parent, _)| parent)?;
    if parent.is_empty() {
        Some("/".to_string())
    } else {
        Some(parent.to_string())
    }
}

pub(crate) fn remote_file_name(path: &str) -> String {
    let normalized = normalize_remote_path(path);
    normalized
        .rsplit('/')
        .next()
        .filter(|name| !name.is_empty())
        .unwrap_or("/")
        .to_string()
}

pub(crate) fn extension_for_name(name: &str, is_dir: bool) -> String {
    if is_dir {
        return String::new();
    }

    match name.rsplit_once('.') {
        Some((prefix, extension)) if !prefix.is_empty() && !extension.is_empty() => {
            extension.to_string()
        }
        _ => String::new(),
    }
}

pub(crate) fn system_time_to_rfc3339(time: SystemTime) -> String {
    let datetime: DateTime<Utc> = time.into();
    datetime.to_rfc3339_opts(SecondsFormat::Secs, true)
}

pub(crate) fn unix_timestamp_to_rfc3339(seconds: u64) -> String {
    system_time_to_rfc3339(UNIX_EPOCH + Duration::from_secs(seconds))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn join_remote_paths_uses_unix_separators() {
        assert_eq!(join_remote_path("/", "release"), "/release");
        assert_eq!(join_remote_path("/var/www", "release"), "/var/www/release");
        assert_eq!(
            join_remote_path("/var/www/", "/release/"),
            "/var/www/release"
        );
        assert_eq!(join_remote_path("", "release"), "/release");
    }

    #[test]
    fn sibling_remote_path_preserves_parent() {
        assert_eq!(
            sibling_remote_path("/var/www/app.tar.gz", "app.zip"),
            "/var/www/app.zip"
        );
        assert_eq!(sibling_remote_path("/app.tar.gz", "app.zip"), "/app.zip");
        assert_eq!(sibling_remote_path("app.tar.gz", "app.zip"), "/app.zip");
    }

    #[test]
    fn parent_remote_path_returns_none_for_root() {
        assert_eq!(parent_remote_path("/var/www"), Some("/var".to_string()));
        assert_eq!(parent_remote_path("/var"), Some("/".to_string()));
        assert_eq!(parent_remote_path("/"), None);
    }
}
