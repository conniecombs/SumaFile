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
  const start = source.indexOf(`pub struct ${structName}`);
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

if (handlers.size !== 79) {
  fail(`expected 79 domain handlers, found ${handlers.size}`);
}
if (commands.domainMethodCount !== 79) {
  fail(`commands.json domainMethodCount must be 79, found ${commands.domainMethodCount}`);
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
  'TreeNode',
  'FolderMetrics',
  'SearchOptions',
  'SearchResult',
  'SmartFolder',
  'Tag',
  'ArchiveInfo',
  'RarInstallPlan',
  'AppAboutInfo',
  'UpdateInfo',
];
for (const typeName of rustCheckedTypes) {
  const schemaFields = types.types?.[typeName]?.fields;
  const rustFields = rustStructFields(models, typeName);
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
