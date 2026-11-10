import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const schemaDir = join(repoRoot, 'ipc', 'schema', 'v1');

function fail(message) {
  console.error(`IPC schema check failed: ${message}`);
  process.exitCode = 1;
}

function readJson(relativePath) {
  const path = join(schemaDir, relativePath);
  if (!existsSync(path)) {
    fail(`missing ${relativePath}`);
    return null;
  }
  return JSON.parse(readFileSync(path, 'utf8'));
}

function readRepo(relativePath) {
  return readFileSync(join(repoRoot, relativePath), 'utf8');
}

function readDispatchSource() {
  return ['mod.rs', 'handlers.rs', 'async_ops.rs', 'params.rs']
    .map((file) => readRepo(`crates/simplefile-service/src/dispatch/${file}`))
    .join('\n');
}

function repoFiles(relativeRoot, extension) {
  const root = join(repoRoot, relativeRoot);
  if (!existsSync(root)) {
    return [];
  }

  const results = [];
  const visit = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const absolutePath = join(directory, entry.name);
      if (entry.isDirectory()) {
        visit(absolutePath);
      } else if (entry.isFile() && entry.name.endsWith(extension)) {
        results.push(absolutePath);
      }
    }
  };
  visit(root);
  return results;
}

function setDifference(left, right) {
  return [...left].filter((value) => !right.has(value)).sort();
}

function rustMethodConstants(source) {
  return new Map(
    [...source.matchAll(/pub const (METHOD_[A-Z0-9_]+):\s*&str\s*=\s*"([a-z0-9_]+)"/g)].map(
      (match) => [match[1], match[2]],
    ),
  );
}

function activeServiceCommands(source, rustConstants) {
  const commands = [
    ...source.matchAll(/^\s*"([a-z0-9_]+)"\s*=>/gm),
  ].map((match) => match[1]);
  const constantArms = [...source.matchAll(/^\s*(METHOD_[A-Z0-9_]+)\s*=>/gm)].map((match) => {
    const method = rustConstants.get(match[1]);
    if (!method) {
      throw new Error(`Unknown generated method constant in service dispatcher: ${match[1]}`);
    }
    return method;
  });
  commands.push(...constantArms);
  if (commands.length === 0) {
    throw new Error('Could not find JSON-RPC method arms in crates/simplefile-service/src/dispatch');
  }
  return new Set(commands);
}

function rustStructFields(source, structName) {
  const start = source.search(new RegExp(`pub(?:\\(crate\\))? struct ${structName}\\b`, 'u'));
  if (start === -1) return null;
  const brace = source.indexOf('{', start);
  const end = source.indexOf('\n}', brace);
  if (brace === -1 || end === -1) return null;
  return [...source.slice(brace, end).matchAll(/pub\s+([a-zA-Z0-9_]+)\s*:/g)].map((match) => match[1]);
}

const protocol = readJson('protocol.json');
const types = readJson('types.json');
const commands = readJson('commands.json');
const events = readJson('events.json');
if (!protocol || !types || !commands || !events) {
  process.exit(1);
}

const serviceDispatch = readDispatchSource();
const protocolCs = readRepo('src-winui/SimpleFile.Ipc/Protocol.Generated.cs');
const protocolGeneratedRs = readRepo('crates/simplefile-ipc/src/protocol_generated.rs');
const models = readRepo('crates/simplefile-core/src/models.rs');

const rustConstants = rustMethodConstants(protocolGeneratedRs);
const handlers = activeServiceCommands(serviceDispatch, rustConstants);
const schemaMethods = new Set(
  Object.keys(commands.methods || {}).filter((name) => !name.startsWith('ipc.')),
);

if (handlers.size !== 86) {
  fail(`expected 86 domain handlers, found ${handlers.size}`);
}
if (commands.domainMethodCount !== 86) {
  fail(`commands.json domainMethodCount must be 86, found ${commands.domainMethodCount}`);
}
if (protocol.protocolVersion !== 1 || commands.protocolVersion !== 1) {
  fail('schema protocolVersion must be 1');
}

for (const name of setDifference(handlers, schemaMethods)) {
  fail(`schema missing domain handler: ${name}`);
}
for (const name of setDifference(schemaMethods, handlers)) {
  fail(`schema has extra domain method: ${name}`);
}
const csharpMethods = new Set(
  [...protocolCs.matchAll(/=\s*"([a-z0-9_.]+)"/g)]
    .map((match) => match[1])
    .filter((name) => schemaMethods.has(name)),
);
for (const name of setDifference(schemaMethods, csharpMethods)) {
  fail(`schema method missing from SimpleFile.Ipc Protocol.Generated.cs: ${name}`);
}
const rustGeneratedMethods = new Set(rustConstants.values());
for (const name of setDifference(schemaMethods, rustGeneratedMethods)) {
  fail(`schema method missing from simplefile-ipc protocol_generated.rs: ${name}`);
}
for (const name of setDifference(rustGeneratedMethods, schemaMethods)) {
  fail(`simplefile-ipc generated method not present in schema: ${name}`);
}
if (!protocolGeneratedRs.includes(`pub const DOMAIN_METHOD_COUNT: usize = ${commands.domainMethodCount};`)) {
  fail('simplefile-ipc generated DOMAIN_METHOD_COUNT is stale');
}
if (!serviceDispatch.includes('is_domain_method(&request.method)')) {
  fail('service dispatcher must use generated domain method metadata for unknown-method routing');
}

if (!commands.methods['ipc.handshake']) {
  fail('commands.json must include ipc.handshake');
}
if (protocol.handshake.method !== 'ipc.handshake') {
  fail('protocol handshake method must be ipc.handshake');
}



const requiredEmitted = [
  'file-change',
  'operation-progress',
  'transfer-tuning',
  'search-results-batch',
  'search-complete',
  'update-chunk',
  'list_directory.chunk',
];
for (const name of requiredEmitted) {
  if (!events.emitted?.[name]) {
    fail(`events.json missing emitted event: ${name}`);
  }
}
for (const name of ['operation-complete', 'operation-error']) {
  if (!events.typedNotEmitted?.[name]) {
    fail(`events.json must list ${name} under typedNotEmitted`);
  } else if (events.typedNotEmitted[name].compatOnly !== true) {
    fail(`events.json typedNotEmitted.${name} must be marked compatOnly`);
  }
}

const compatOnlyMethods = {
  copy_entry: { replacement: 'copy_with_progress', legacy: true, caller: 'CopyEntryAsync' },
  move_entry: { replacement: 'move_with_progress', legacy: true, caller: 'MoveEntryAsync' },
  cancel_count_items: { replacement: 'cancel_folder_item_count', caller: 'CancelCountItemsAsync' },
  get_git_status: { replacement: 'get_git_file_statuses', caller: 'GetGitStatusAsync' },
  show_main_window: { replacement: 'WinUI AppWindow.Show/Activate', hostOwned: true, caller: 'ShowMainWindowAsync' },
};
for (const [methodName, expected] of Object.entries(compatOnlyMethods)) {
  const method = commands.methods?.[methodName];
  if (!method) {
    fail(`commands.json missing compatibility method ${methodName}`);
    continue;
  }
  if (method.compatOnly !== true) {
    fail(`commands.json ${methodName} must be marked compatOnly`);
  }
  if (expected.legacy && method.legacy !== true) {
    fail(`commands.json ${methodName} must be marked legacy`);
  }
  if (expected.hostOwned && method.hostOwned !== true) {
    fail(`commands.json ${methodName} must be marked hostOwned`);
  }
  if (method.replacement !== expected.replacement) {
    fail(`commands.json ${methodName} replacement must be ${expected.replacement}`);
  }
}

const liveCallerFiles = [
  ...repoFiles('src-winui/SimpleFile.App', '.cs'),
  ...repoFiles('src-winui/SimpleFile.Core', '.cs').filter(
    (file) => !file.endsWith('FileOperationService.cs'),
  ),
];
for (const file of liveCallerFiles) {
  const relativePath = file.slice(repoRoot.length + 1).replace(/\\/g, '/');
  const source = readFileSync(file, 'utf8');
  for (const { caller } of Object.values(compatOnlyMethods)) {
    if (
      caller === 'ShowMainWindowAsync'
      && relativePath === 'src-winui/SimpleFile.Core/BackendSession.cs'
    ) {
      continue;
    }

    if (new RegExp(`\\b${caller}\\s*\\(`, 'u').test(source)) {
      fail(`${relativePath} must not call compat-only IPC wrapper ${caller}`);
    }
  }
}


const rustCheckedTypes = [
  'FileEntry',
  'DirectoryListing',
  'DirectoryListingChunk',
  'ProgressUpdate',
  'TuningUpdate',
  'ActiveFile',
  'TreeNode',
  'FolderMetrics',
  'SearchOptions',
  'SearchResult',
  'SmartFolder',
  'Tag',
  'ArchiveInfo',
  'ArchiveFormatCapability',
  'ArchiveCapabilities',
  'AppAboutInfo',
  'UpdateInfo',
];
for (const typeName of rustCheckedTypes) {
  const schemaFields = types.types?.[typeName]?.fields;
  const rustFields = rustStructFields(readRepo(types.types[typeName].source), typeName);
  if (!schemaFields || !rustFields) {
    fail(`could not compare fields for ${typeName}`);
    continue;
  }
  if (JSON.stringify(schemaFields) !== JSON.stringify(rustFields)) {
    fail(`${typeName} fields mismatch models.rs: schema [${schemaFields}] rust [${rustFields}]`);
  }
}



const requiredGoldens = [
  'ipc.handshake.request.json',
  'ipc.handshake.result.json',
  'search_files.request.json',
  'batch_rename.request.json',
  'save_smart_folder.request.json',
  'conflict.error.json',
  'trash_unavailable.error.json',
  'host_owned.error.json',
  'operation-progress.event.json',
  'list_directory.chunk.event.json',
  'update-chunk.event.json',
  'file-entry.result.json',
];
for (const file of requiredGoldens) {
  if (!existsSync(join(schemaDir, 'goldens', file))) {
    fail(`missing golden ${file}`);
  }
}

const searchRequest = readJson('goldens/search_files.request.json');
const searchOptions = searchRequest?.params?.options || {};
for (const key of ['search_path', 'case_sensitive', 'include_hidden', 'search_id', 'content_search']) {
  if (!(key in searchOptions)) {
    fail(`search_files golden missing nested snake_case key ${key}`);
  }
}
if ('searchPath' in searchOptions) {
  fail('search_files golden must not camelCase nested SearchOptions');
}

const batchRequest = readJson('goldens/batch_rename.request.json');
const rename = batchRequest?.params?.entries?.[0] || {};
if (!('new_name' in rename) || 'newName' in rename) {
  fail('batch_rename golden must use nested new_name, not newName');
}

const fileEntry = readJson('goldens/file-entry.result.json');
if (fileEntry && 'itemCount' in fileEntry) {
  fail('file-entry golden must not include frontend-only itemCount');
}

const conflict = readJson('goldens/conflict.error.json');
if (conflict?.error?.code !== -32000 || !String(conflict?.error?.message || '').startsWith('CONFLICT:')) {
  fail('conflict golden must be JSON-RPC -32000 with CONFLICT: message');
}

const progress = readJson('goldens/operation-progress.event.json');
if (!progress || 'id' in progress || progress.method !== 'operation-progress') {
  fail('operation-progress golden must be a notification (no id)');
}
if (!progress?.params?.operation_id) {
  fail('operation-progress golden must include operation_id');
}

const updateChunk = readJson('goldens/update-chunk.event.json');
if (!Array.isArray(updateChunk?.params) || updateChunk.params.length !== 2) {
  fail('update-chunk golden params must be a two-element array');
}

const cancelCommands = new Set(protocol.cancellation?.commands || []);
for (const name of [
  'cancel_operation',
  'cancel_search',
  'cancel_folder_size',
  'cancel_folder_item_count',
  'cancel_count_items',
  'cancel_folder_metrics',
  'cancel_disk_cleanup',
  'cancel_duplicate_check',
]) {
  if (!cancelCommands.has(name) || !commands.methods[name]) {
    fail(`cancellation command missing from protocol/schema: ${name}`);
  }
}

if (protocol.transport?.maxFrameBytes !== 80 * 1024 * 1024) {
  fail('maxFrameBytes must be 80 MiB');
}

const goldenFiles = existsSync(join(schemaDir, 'goldens'))
  ? readdirSync(join(schemaDir, 'goldens')).filter((name) => name.endsWith('.json'))
  : [];
if (goldenFiles.length < requiredGoldens.length) {
  fail('golden directory is incomplete');
}

if (!process.exitCode) {
  console.log(
    `Checked IPC v1 schema: ${schemaMethods.size} domain methods, ${requiredEmitted.length} emitted events, ${requiredGoldens.length} goldens.`,
  );
}

// Plugin protocol (ipc/schema/plugin/v1): the host methods and SDK version must match both sides.
const pluginProtocolPath = join(repoRoot, 'ipc', 'schema', 'plugin', 'v1', 'protocol.json');
const pluginProtocol = existsSync(pluginProtocolPath)
  ? JSON.parse(readFileSync(pluginProtocolPath, 'utf8'))
  : null;
if (!pluginProtocol) {
  fail('missing ipc/schema/plugin/v1/protocol.json');
} else {
  const hostMethods = Array.isArray(pluginProtocol.hostMethods) ? pluginProtocol.hostMethods : [];
  if (hostMethods.length === 0) {
    fail('plugin protocol hostMethods must be a non-empty array');
  }
  const hostMethodSources = [
    'src-winui/SimpleFile.PluginHost/Backend/PluginHostMethods.cs',
    'crates/sumafile-plugin/src/host.rs',
  ];
  for (const sourcePath of hostMethodSources) {
    const source = readRepo(sourcePath);
    for (const method of hostMethods) {
      // The exact quoted name: "host.progress_begin returned ..." does not count.
      if (!source.includes(`"${method}"`)) {
        fail(`plugin host method ${method} is not a string literal in ${sourcePath}`);
      }
    }
  }
  const sdkVersion = readRepo('src-winui/SimpleFile.PluginSdk/PluginSdk.cs').match(
    /public const string Version\s*=\s*"([^"]+)"/,
  )?.[1];
  if (!sdkVersion) {
    fail('could not find PluginSdk.Version in src-winui/SimpleFile.PluginSdk/PluginSdk.cs');
  } else if (pluginProtocol.sdkVersion !== sdkVersion) {
    fail(`plugin protocol sdkVersion ${pluginProtocol.sdkVersion} does not match PluginSdk.Version ${sdkVersion}`);
  }
  if (pluginProtocol.handshake?.method !== 'plugin.handshake') {
    fail('plugin protocol handshake.method must be plugin.handshake');
  }
  if (pluginProtocol.cancel?.method !== 'cancel') {
    fail('plugin protocol cancel.method must be cancel');
  }

  // Archive engine methods: an object keyed by method name. The service sends every one and
  // the Hello fixture backend serves every one.
  const engineMethodTable = pluginProtocol.archiveEngineMethods;
  const engineMethods =
    engineMethodTable && typeof engineMethodTable === 'object' && !Array.isArray(engineMethodTable)
      ? Object.keys(engineMethodTable)
      : [];
  if (engineMethods.length === 0) {
    fail('plugin protocol archiveEngineMethods must be a non-empty object keyed by method name');
  }
  const deadlines = pluginProtocol.deadlines ?? {};
  for (const method of engineMethods) {
    const entry = engineMethodTable[method];
    if (!entry?.params || typeof entry.params !== 'object') {
      fail(`plugin archive engine method ${method} needs a params object`);
    }
    if (!('result' in (entry ?? {}))) {
      fail(`plugin archive engine method ${method} needs a result`);
    }
    if (!entry?.deadline || !(entry.deadline in deadlines)) {
      fail(`plugin archive engine method ${method} names an unknown deadline ${entry?.deadline}`);
    }
  }
  const engineMethodSources = [
    'crates/simplefile-service/src/plugin_archive/mod.rs',
    'crates/hello-plugin-backend/src/main.rs',
    'plugins/sevenzip/backend/src/handler.rs',
  ];
  for (const sourcePath of engineMethodSources) {
    const source = readRepo(sourcePath);
    for (const method of engineMethods) {
      if (!source.includes(`"${method}"`)) {
        fail(`plugin archive engine method ${method} is not a string literal in ${sourcePath}`);
      }
    }
  }

  // Notifications: archive.progress is produced by the helper crate and consumed by the service.
  const progressNotification = 'archive.progress';
  if (!pluginProtocol.notifications?.[progressNotification]) {
    fail(`plugin protocol notifications must document ${progressNotification}`);
  }
  for (const sourcePath of [
    'crates/sumafile-plugin/src/host.rs',
    'crates/simplefile-service/src/plugin_archive/connection.rs',
  ]) {
    if (!readRepo(sourcePath).includes(`"${progressNotification}"`)) {
      fail(`plugin notification ${progressNotification} is not a string literal in ${sourcePath}`);
    }
  }

  // The cancelled message is matched by exact text on every side.
  const cancelledMessage = pluginProtocol.cancel?.cancelledMessage;
  if (typeof cancelledMessage !== 'string' || cancelledMessage.length === 0) {
    fail('plugin protocol cancel.cancelledMessage must be a non-empty string');
  } else {
    const cancelledMessageSources = [
      'crates/simplefile-core/src/archive/engine.rs',
      'crates/sumafile-plugin/src/lib.rs',
      'src-winui/SimpleFile.Core/FileOperationService.cs',
    ];
    for (const sourcePath of cancelledMessageSources) {
      if (!readRepo(sourcePath).includes(`"${cancelledMessage}"`)) {
        fail(`plugin cancel.cancelledMessage "${cancelledMessage}" is not a string literal in ${sourcePath}`);
      }
    }
  }

  // SDK version constants on both Rust sides.
  const rustSdkVersions = [
    ['crates/sumafile-plugin/src/lib.rs', /pub const SDK_VERSION: &str = "([^"]+)"/, 'SDK_VERSION'],
    [
      'crates/simplefile-service/src/plugin_archive/mod.rs',
      /pub const PLUGIN_SDK_VERSION: &str = "([^"]+)"/,
      'PLUGIN_SDK_VERSION',
    ],
  ];
  for (const [sourcePath, pattern, name] of rustSdkVersions) {
    const version = readRepo(sourcePath).match(pattern)?.[1];
    if (!version) {
      fail(`could not find ${name} in ${sourcePath}`);
    } else if (version !== pluginProtocol.sdkVersion) {
      fail(`${name} ${version} in ${sourcePath} does not match plugin protocol sdkVersion ${pluginProtocol.sdkVersion}`);
    }
  }

  if (!process.exitCode) {
    console.log(
      `Checked plugin protocol v1: ${hostMethods.length} host methods, ${engineMethods.length} archive engine methods, SDK ${pluginProtocol.sdkVersion}.`,
    );
  }
}

// Plugin-owned protocols (plugins/<name>/protocol.json): each plugin's methods and events must be
// string literals in every file of its literalSources (the backend's handler, and the C# client once it
// exists), and each documented error text must start a string literal in every source that produces it.
// `events` is optional; a plugin that declares it must document at least one.
const pluginProtocols = [
  {
    dir: 'plugins/remote',
    id: 'com.sumafile.remote',
    label: 'remote',
    literalSources: [
      'plugins/remote/backend/src/handler.rs',
      'plugins/remote/ui/SumaFile.Remote.Model/RemoteProtocol.cs',
    ],
  },
  {
    dir: 'plugins/git',
    id: 'com.sumafile.git',
    label: 'git',
    literalSources: ['plugins/git/backend/src/handler.rs', 'plugins/git/ui/SumaFile.Git.Model/GitProtocol.cs'],
  },
];

const objectKeys = (value) =>
  value && typeof value === 'object' && !Array.isArray(value) ? Object.keys(value) : [];

for (const { dir, id, label, literalSources } of pluginProtocols) {
  const protocolFile = `${dir}/protocol.json`;
  const protocolPath = join(repoRoot, protocolFile);
  const protocol = existsSync(protocolPath) ? JSON.parse(readFileSync(protocolPath, 'utf8')) : null;
  if (!protocol) {
    fail(`missing ${protocolFile}`);
    continue;
  }
  const exitCodeBefore = process.exitCode;
  process.exitCode = undefined;
  if (protocol.plugin !== id) {
    fail(`${protocolFile} plugin must be ${id}`);
  }
  const methods = objectKeys(protocol.methods);
  const events = objectKeys(protocol.events);
  const errors = objectKeys(protocol.errors);
  if (methods.length === 0) {
    fail(`${protocolFile} methods must be a non-empty object keyed by method name`);
  }
  if ('events' in protocol && events.length === 0) {
    fail(`${protocolFile} events must be a non-empty object keyed by event name`);
  }
  for (const method of methods) {
    const entry = protocol.methods[method];
    if (!entry || !('params' in entry) || !('result' in entry)) {
      fail(`${label} plugin method ${method} needs params and result`);
    }
    for (const event of [entry?.emits, entry?.progressEvent].filter(Boolean)) {
      if (!events.includes(event)) {
        fail(`${label} plugin method ${method} names an undocumented event ${event}`);
      }
    }
  }
  for (const sourcePath of literalSources) {
    if (!existsSync(join(repoRoot, sourcePath))) {
      fail(`${label} plugin literal source ${sourcePath} is missing`);
      continue;
    }
    const source = readRepo(sourcePath);
    for (const name of [...methods, ...events]) {
      if (!source.includes(`"${name}"`)) {
        fail(`${label} plugin method or event ${name} is not a string literal in ${sourcePath}`);
      }
    }
  }
  for (const key of errors) {
    const entry = protocol.errors[key];
    const text = typeof entry?.text === 'string' ? entry.text : '';
    const sources = Array.isArray(entry?.sources) ? entry.sources : [];
    if (text.length === 0 || sources.length === 0) {
      fail(`${label} plugin error ${key} needs a text and at least one source`);
      continue;
    }
    // The backend builds these texts with format!, so the literal starts with the text before
    // its first <placeholder>: "remote session not found: {session_id}" matches
    // "remote session not found: <id>".
    const placeholder = text.indexOf('<');
    const prefix = placeholder === -1 ? text : text.slice(0, placeholder);
    for (const sourcePath of sources) {
      if (!existsSync(join(repoRoot, sourcePath))) {
        fail(`${label} plugin error ${key} names a missing source ${sourcePath}`);
      } else if (!readRepo(sourcePath).includes(`"${prefix}`)) {
        fail(`${label} plugin error ${key} text "${prefix}" does not start a string literal in ${sourcePath}`);
      }
    }
  }
  if (!process.exitCode) {
    console.log(
      `Checked ${label} plugin protocol: ${methods.length} methods, ${events.length} events, ${errors.length} error texts.`,
    );
  }
  process.exitCode = process.exitCode || exitCodeBefore;
}
