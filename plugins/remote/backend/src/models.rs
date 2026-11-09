//! Remote profile types (copied from `simplefile_core::remote::models`) and
//! the listing types, which serialize exactly like `simplefile_core::models`
//! so the plugin's results keep the 1.0.4 IPC shapes.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum RemoteProtocol {
    Sftp,
    Ftp,
    Ftps,
}

impl RemoteProtocol {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Sftp => "sftp",
            Self::Ftp => "ftp",
            Self::Ftps => "ftps",
        }
    }

    pub(crate) fn parse(value: &str) -> Result<Self, String> {
        match value {
            "sftp" => Ok(Self::Sftp),
            "ftp" => Ok(Self::Ftp),
            "ftps" => Ok(Self::Ftps),
            _ => Err(format!("Unsupported remote protocol: {value}")),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum RemoteAuthKind {
    Password,
    PrivateKey,
    Agent,
    Anonymous,
}

impl RemoteAuthKind {
    pub(crate) fn as_str(&self) -> &'static str {
        match self {
            Self::Password => "password",
            Self::PrivateKey => "private-key",
            Self::Agent => "agent",
            Self::Anonymous => "anonymous",
        }
    }

    pub(crate) fn parse(value: &str) -> Result<Self, String> {
        match value {
            "password" => Ok(Self::Password),
            "private-key" => Ok(Self::PrivateKey),
            "agent" => Ok(Self::Agent),
            "anonymous" => Ok(Self::Anonymous),
            _ => Err(format!("Unsupported remote authentication kind: {value}")),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct RemoteProfile {
    pub id: String,
    pub name: String,
    pub protocol: RemoteProtocol,
    pub host: String,
    pub port: u16,
    pub username: String,
    pub root_path: String,
    pub auth_kind: RemoteAuthKind,
    pub insecure_plain_ftp: bool,
    pub passive_mode: bool,
    pub credential_target: Option<String>,
    pub private_key_path: Option<String>,
    pub trusted_host_fingerprint: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct RemoteProfileInput {
    pub id: Option<String>,
    pub name: String,
    pub protocol: RemoteProtocol,
    pub host: String,
    pub port: u16,
    pub username: String,
    pub root_path: String,
    pub auth_kind: RemoteAuthKind,
    pub insecure_plain_ftp: bool,
    pub passive_mode: bool,
    pub credential_target: Option<String>,
    pub private_key_path: Option<String>,
    pub trusted_host_fingerprint: Option<String>,
}

/// A file or directory entry; the same JSON as `simplefile_core::models::FileEntry`.
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct FileEntry {
    pub name: String,
    pub path: String,
    pub is_dir: bool,
    pub is_symlink: bool,
    /// A leading-dot name.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_hidden: bool,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_system: bool,
    pub size: u64,
    pub modified: String,
    pub extension: String,
    /// Unix permission string like "rwxr-xr-x", when the server reports one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub permissions: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub symlink_target: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub git_status: Option<String>,
}

/// A directory listing; the same JSON as `simplefile_core::models::DirectoryListing`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DirectoryListing {
    pub path: String,
    pub parent: Option<String>,
    pub entries: Vec<FileEntry>,
    #[serde(default)]
    pub is_network: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn file_entry_serializes_like_the_service() {
        let entry = FileEntry {
            name: "report.txt".to_string(),
            path: "/docs/report.txt".to_string(),
            is_dir: false,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 42,
            modified: "2026-09-21T00:00:00Z".to_string(),
            extension: "txt".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        };

        assert_eq!(
            serde_json::to_string(&entry).unwrap(),
            r#"{"name":"report.txt","path":"/docs/report.txt","is_dir":false,"is_symlink":false,"size":42,"modified":"2026-09-21T00:00:00Z","extension":"txt"}"#
        );

        let listing = DirectoryListing {
            path: "/docs".to_string(),
            parent: Some("/".to_string()),
            entries: vec![entry],
            is_network: true,
        };
        let json = serde_json::to_value(&listing).unwrap();
        let mut keys = json.as_object().unwrap().keys().collect::<Vec<_>>();
        keys.sort();
        assert_eq!(keys, vec!["entries", "is_network", "parent", "path"]);
    }
}
