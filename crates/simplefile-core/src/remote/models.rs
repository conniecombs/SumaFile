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
