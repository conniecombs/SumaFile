use serde_json::{Map, Value};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

fn schema_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../ipc/schema/v1")
}

fn read_json(name: &str) -> Value {
    let path = schema_dir().join(name);
    let text = fs::read_to_string(&path).unwrap_or_else(|error| {
        panic!("failed to read {}: {error}", path.display());
    });
    serde_json::from_str(&text).unwrap_or_else(|error| {
        panic!("invalid JSON {}: {error}", path.display());
    })
}

fn object<'a>(value: &'a Value, label: &str) -> &'a Map<String, Value> {
    value
        .as_object()
        .unwrap_or_else(|| panic!("{label} must be an object"))
}

#[test]
fn protocol_constants_match_schema() {
    let protocol = read_json("protocol.json");
    assert_eq!(
        protocol["protocolVersion"],
        simplefile_ipc::PROTOCOL_VERSION
    );
    assert_eq!(protocol["jsonrpc"], simplefile_ipc::JSONRPC_VERSION);
    assert_eq!(
        protocol["transport"]["maxFrameBytes"],
        simplefile_ipc::MAX_FRAME_BYTES
    );
    assert_eq!(
        protocol["errors"]["application"]["code"],
        simplefile_ipc::ERR_APPLICATION
    );
    assert_eq!(
        protocol["errors"]["hostOwned"]["code"],
        simplefile_ipc::ERR_HOST_OWNED
    );
    assert_eq!(
        protocol["handshake"]["method"],
        simplefile_ipc::HANDSHAKE_METHOD
    );
    assert!(!protocol["cancellation"]["jsonrpcCancel"].as_bool().unwrap());
}

#[test]
fn commands_cover_seventy_six_domain_methods_plus_handshake() {
    let commands = read_json("commands.json");
    assert_eq!(
        commands["domainMethodCount"],
        simplefile_ipc::DOMAIN_METHOD_COUNT
    );
    let methods = object(&commands["methods"], "methods");
    assert!(methods.contains_key(simplefile_ipc::HANDSHAKE_METHOD));
    let domain: Vec<_> = methods
        .keys()
        .filter(|name| !name.starts_with("ipc."))
        .collect();
    assert_eq!(domain.len(), simplefile_ipc::DOMAIN_METHOD_COUNT);
    let schema_domain: HashSet<&str> = domain.iter().map(|name| name.as_str()).collect();
    let generated_domain: HashSet<&str> = simplefile_ipc::DOMAIN_METHODS.iter().copied().collect();
    assert_eq!(schema_domain, generated_domain);
    assert!(simplefile_ipc::is_domain_method("get_home_dir"));
    assert!(!simplefile_ipc::is_domain_method(
        simplefile_ipc::HANDSHAKE_METHOD
    ));
    assert!(methods["list_directory"]["params"]
        .as_object()
        .unwrap()
        .contains_key("path"));
    assert!(methods["list_directory"]["params"]
        .as_object()
        .unwrap()
        .contains_key("finalEntries"));
    assert!(!methods["list_directory"]["params"]
        .as_object()
        .unwrap()
        .contains_key("onChunk"));
    assert_eq!(
        methods["select_directory"]["hostOwned"].as_bool(),
        Some(true)
    );
}

#[test]
fn events_include_progress_search_and_listing_chunks() {
    let events = read_json("events.json");
    let emitted = object(&events["emitted"], "emitted");
    for name in [
        simplefile_ipc::FILE_CHANGE,
        simplefile_ipc::OPERATION_PROGRESS,
        simplefile_ipc::SEARCH_RESULTS_BATCH,
        simplefile_ipc::SEARCH_COMPLETE,
        simplefile_ipc::UPDATE_CHUNK,
        simplefile_ipc::LIST_DIRECTORY_CHUNK,
    ] {
        assert!(emitted.contains_key(name), "missing emitted event {name}");
    }
    assert!(events["typedNotEmitted"]
        .as_object()
        .unwrap()
        .contains_key("operation-complete"));
    assert!(events["typedNotEmitted"]
        .as_object()
        .unwrap()
        .contains_key("operation-error"));
}

#[test]
fn nested_request_goldens_use_snake_case() {
    let search = read_json("goldens/search_files.request.json");
    let options = object(&search["params"]["options"], "search options");
    assert!(options.contains_key("search_path"));
    assert!(options.contains_key("search_id"));
    assert!(!options.contains_key("searchPath"));

    let batch = read_json("goldens/batch_rename.request.json");
    let entry = object(&batch["params"]["entries"][0], "rename entry");
    assert!(entry.contains_key("new_name"));
    assert!(!entry.contains_key("newName"));

    let folder = read_json("goldens/save_smart_folder.request.json");
    let search_options = object(
        &folder["params"]["folder"]["search_options"],
        "smart folder search_options",
    );
    assert!(search_options.contains_key("search_path"));
}

#[test]
fn error_and_progress_goldens_preserve_ids_and_prefixes() {
    let conflict = read_json("goldens/conflict.error.json");
    assert_eq!(conflict["error"]["code"], simplefile_ipc::ERR_APPLICATION);
    assert!(conflict["error"]["message"]
        .as_str()
        .unwrap()
        .starts_with(simplefile_ipc::PREFIX_CONFLICT));

    let trash = read_json("goldens/trash_unavailable.error.json");
    assert!(trash["error"]["message"]
        .as_str()
        .unwrap()
        .starts_with(simplefile_ipc::PREFIX_TRASH_UNAVAILABLE));

    let host_owned = read_json("goldens/host_owned.error.json");
    assert_eq!(host_owned["error"]["code"], simplefile_ipc::ERR_HOST_OWNED);

    let progress = read_json("goldens/operation-progress.event.json");
    assert!(progress.get("id").is_none());
    assert_eq!(progress["method"], simplefile_ipc::OPERATION_PROGRESS);
    assert!(progress["params"]["operation_id"].as_str().is_some());

    let chunk = read_json("goldens/update-chunk.event.json");
    assert!(chunk["params"].as_array().unwrap().len() == 2);

    let listing = read_json("goldens/list_directory.chunk.event.json");
    assert_eq!(listing["method"], simplefile_ipc::LIST_DIRECTORY_CHUNK);
    assert!(listing["params"]["requestId"].is_number());
    assert!(listing["params"]["chunk_index"].is_number());

    let entry = read_json("goldens/file-entry.result.json");
    assert!(entry.get("itemCount").is_none());
}

#[test]
fn golden_directory_is_present() {
    let dir = schema_dir().join("goldens");
    assert!(Path::new(&dir).is_dir());
    let count = fs::read_dir(&dir).unwrap().count();
    assert!(count >= 12, "expected at least 12 goldens, found {count}");
}
