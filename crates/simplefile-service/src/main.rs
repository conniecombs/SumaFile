use simplefile_service::{pipe_path, serve_connection, SessionState};

#[tokio::main]
async fn main() {
    if let Err(error) = run().await {
        eprintln!("simplefile-service: {error}");
        std::process::exit(1);
    }
}

async fn run() -> Result<(), String> {
    let args = Args::parse(std::env::args().skip(1))?;
    let pipe = pipe_path(&args.pipe_name);
    eprintln!(
        "simplefile-service {} listening on {pipe}",
        simplefile_core::APP_DISPLAY_VERSION
    );

    let auth_token = read_required_auth_token()?;

    #[cfg(windows)]
    {
        use std::os::windows::io::RawHandle;
        use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX, WRITE_DAC,
        };
        use windows_sys::Win32::System::Pipes::{
            CreateNamedPipeW, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT,
        };

        let pipe_wide: Vec<u16> = pipe.encode_utf16().chain(std::iter::once(0)).collect();
        let raw = unsafe {
            CreateNamedPipeW(
                pipe_wide.as_ptr(),
                PIPE_ACCESS_DUPLEX
                    | FILE_FLAG_FIRST_PIPE_INSTANCE
                    | FILE_FLAG_OVERLAPPED
                    | WRITE_DAC,
                PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS | PIPE_WAIT,
                1,
                65536,
                65536,
                0,
                std::ptr::null(),
            )
        };
        if raw == INVALID_HANDLE_VALUE {
            return Err(format!(
                "failed to create named pipe {pipe}: {}",
                std::io::Error::last_os_error()
            ));
        }
        let server = unsafe {
            tokio::net::windows::named_pipe::NamedPipeServer::from_raw_handle(raw as RawHandle)
        }
        .map_err(|error| format!("failed to register named pipe with tokio: {error}"))?;

        apply_creator_only_dacl(&server)?;

        server
            .connect()
            .await
            .map_err(|error| format!("waiting for client failed: {error}"))?;

        // If parent-pid was specified, spawn a liveness monitor that exits
        // when the parent process is no longer alive.
        if let Some(parent_pid) = args.parent_pid {
            tokio::spawn(monitor_parent_liveness(parent_pid));
        }

        let state = SessionState {
            expected_token: Some(auth_token),
            ..SessionState::default()
        };
        let (reader, writer) = tokio::io::split(server);
        serve_connection(reader, writer, state).await?;
        Ok(())
    }

    #[cfg(not(windows))]
    {
        let _ = pipe;
        Err("simplefile-service requires Windows named pipes".to_string())
    }
}

#[cfg(windows)]
async fn monitor_parent_liveness(parent_pid: u32) {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_SYNCHRONIZE};

    loop {
        tokio::time::sleep(std::time::Duration::from_secs(2)).await;

        let alive = unsafe {
            let handle = OpenProcess(PROCESS_SYNCHRONIZE, 0, parent_pid);
            if handle.is_null() || handle == 0 as _ {
                false
            } else {
                CloseHandle(handle);
                true
            }
        };

        if !alive {
            eprintln!("Parent process {parent_pid} is no longer alive, exiting.");
            std::process::exit(0);
        }
    }
}

fn read_required_auth_token() -> Result<String, String> {
    let mut token = String::new();
    std::io::stdin()
        .read_line(&mut token)
        .map_err(|error| format!("failed to read auth token from stdin: {error}"))?;
    let token = token.trim().to_string();
    if token.is_empty() {
        return Err("auth token is required on stdin; refusing to listen".to_string());
    }
    Ok(token)
}

#[cfg(windows)]
fn apply_creator_only_dacl(
    server: &tokio::net::windows::named_pipe::NamedPipeServer,
) -> Result<(), String> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Foundation::{LocalFree, HANDLE};
    use windows_sys::Win32::Security::Authorization::{
        ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
    };
    use windows_sys::Win32::Security::{
        GetTokenInformation, SetKernelObjectSecurity, TokenUser, DACL_SECURITY_INFORMATION,
        PSECURITY_DESCRIPTOR, TOKEN_QUERY, TOKEN_USER,
    };
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return Err("failed to open process token for pipe DACL".to_string());
        }

        let mut needed = 0u32;
        GetTokenInformation(token, TokenUser, std::ptr::null_mut(), 0, &mut needed);
        let mut buffer = vec![0u8; needed.max(1) as usize];
        if GetTokenInformation(
            token,
            TokenUser,
            buffer.as_mut_ptr().cast(),
            needed,
            &mut needed,
        ) == 0
        {
            windows_sys::Win32::Foundation::CloseHandle(token);
            return Err("failed to query process user SID for pipe DACL".to_string());
        }
        windows_sys::Win32::Foundation::CloseHandle(token);

        let user = &*(buffer.as_ptr() as *const TOKEN_USER);
        let mut sid_string = std::ptr::null_mut();
        if ConvertSidToStringSidW(user.User.Sid, &mut sid_string) == 0 {
            return Err("failed to convert user SID for pipe DACL".to_string());
        }
        let sid = {
            let mut len = 0usize;
            while *sid_string.add(len) != 0 {
                len += 1;
            }
            String::from_utf16_lossy(std::slice::from_raw_parts(sid_string, len))
        };
        LocalFree(sid_string.cast());

        let sddl = format!("D:P(A;;GA;;;{sid})");
        let sddl_wide: Vec<u16> = sddl.encode_utf16().chain(std::iter::once(0)).collect();
        let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl_wide.as_ptr(),
            1,
            &mut sd,
            std::ptr::null_mut(),
        ) == 0
        {
            return Err("failed to build creator-only security descriptor".to_string());
        }

        let ok = SetKernelObjectSecurity(
            server.as_raw_handle() as HANDLE,
            DACL_SECURITY_INFORMATION,
            sd,
        );
        LocalFree(sd.cast());
        if ok == 0 {
            return Err("failed to apply creator-only DACL to named pipe".to_string());
        }
    }
    Ok(())
}

#[derive(Debug)]
struct Args {
    pipe_name: String,
    parent_pid: Option<u32>,
}

impl Args {
    fn parse(args: impl IntoIterator<Item = String>) -> Result<Self, String> {
        let mut pipe_name = format!("SumaFile.dev.{}", std::process::id());
        let mut parent_pid = None;
        let mut items = args.into_iter();
        while let Some(arg) = items.next() {
            match arg.as_str() {
                "--pipe-name" => {
                    pipe_name = items
                        .next()
                        .ok_or_else(|| "missing value for --pipe-name".to_string())?;
                }
                "--auth-token" => {
                    return Err(
                        "--auth-token is no longer accepted; write the token to stdin".to_string(),
                    );
                }
                "--parent-pid" => {
                    let value = items
                        .next()
                        .ok_or_else(|| "missing value for --parent-pid".to_string())?;
                    parent_pid = Some(
                        value
                            .parse::<u32>()
                            .map_err(|e| format!("invalid --parent-pid value: {e}"))?,
                    );
                }
                "--help" | "-h" => {
                    eprintln!("Usage: simplefile-service [--pipe-name NAME] [--parent-pid PID]");
                    std::process::exit(0);
                }
                other => return Err(format!("unknown argument: {other}")),
            }
        }
        Ok(Self {
            pipe_name,
            parent_pid,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::Args;

    #[test]
    fn rejects_auth_token_cli_flag() {
        let error = Args::parse(["--auth-token".to_string(), "secret".to_string()]).unwrap_err();
        assert!(error.contains("stdin"), "{error}");
    }
}
