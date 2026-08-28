import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const gatePath = 'docs/winui-migration/parity-gate.md';

function fail(message) {
  console.error(`WinUI parity gate check failed: ${message}`);
  process.exitCode = 1;
}

function readRepo(relativePath) {
  const fullPath = join(repoRoot, relativePath);
  if (!existsSync(fullPath)) {
    fail(`missing ${relativePath}`);
    return '';
  }
  return readFileSync(fullPath, 'utf8');
}

function activeServiceCommands(source) {
  return [
    ...source.matchAll(/^\s*"([a-z0-9_]+)"\s*=>/gm),
  ].map((match) => match[1]);
}

function extractQuotedIds(source, pattern) {
  return [...source.matchAll(pattern)].map((match) => match[1]);
}

const gate = readRepo(gatePath);
const serviceDispatch = readRepo('crates/simplefile-service/src/dispatch.rs');
const contextMenu = readRepo('src-winui/SimpleFile.Core/ContextMenuBuilder.cs');
const palette = readRepo('src-winui/SimpleFile.Core/AppCommandCatalog.cs');
const events = readRepo('ipc/schema/v1/events.json');
const ipcClient = readRepo('src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs');
const packageJson = readRepo('package.json');

if (!gate.includes('## Retirement lock')) {
  fail(`${gatePath} must include a "## Retirement lock" section.`);
}
if (!gate.includes('Retirement completed')) {
  fail(`${gatePath} must record that Svelte/Tauri retirement completed.`);
}
if (!gate.includes('frontend/') || !gate.includes('src-tauri/')) {
  fail(`${gatePath} must name retired frontend/ and src-tauri/ domain.`);
}
if (!gate.includes('crates/simplefile-core') || !gate.includes('crates/simplefile-service')) {
  fail(`${gatePath} must keep reusable Rust crates named.`);
}

const statuses = ['PASS', 'MANUAL', 'OPEN', 'WAIVED'];
for (const status of statuses) {
  if (!gate.includes(`\`${status}\``) && !gate.includes(`| \`${status}\``) && !gate.includes(status)) {
    fail(`${gatePath} must define status ${status}.`);
  }
}

const commands = activeServiceCommands(serviceDispatch);
if (commands.length !== 76) {
  fail(`expected 76 domain commands, found ${commands.length}.`);
}

for (const command of commands) {
  if (!gate.includes(command)) {
    fail(`${gatePath} must mention ${command}.`);
  }
}

const ctxIds = [...new Set([...contextMenu.matchAll(/ctx-[a-z0-9-]+/g)].map((match) => match[0]))];
for (const id of ctxIds) {
  if (!gate.includes(id)) {
    fail(`${gatePath} must list context menu id ${id}.`);
  }
}

const paletteIds = [
  ...new Set([
    ...extractQuotedIds(palette, /id:\s*'([^']+)'/g),
    ...extractQuotedIds(palette, /new\("([^"]+)"/g),
  ]),
];
for (const id of paletteIds) {
  if (!gate.includes(id)) {
    fail(`${gatePath} must list command palette id ${id}.`);
  }
}

const emitted = JSON.parse(events).emitted ?? {};
for (const eventName of Object.keys(emitted)) {
  if (!gate.includes(eventName)) {
    fail(`${gatePath} must list emitted event ${eventName}.`);
  }
}

for (const snippet of [
  'list_subdirectories',
  'save_smart_folder',
  'marquee',
  'npm run check:winui',
  'npm run smoke:winui',
  'OPEN',
]) {
  if (!gate.includes(snippet)) {
    fail(`${gatePath} must include ${snippet}.`);
  }
}

if (!packageJson.includes('check:winui-parity-gate')) {
  fail('package.json must expose check:winui-parity-gate.');
}

if (!ipcClient.includes('ListDirectoryAsync') || !ipcClient.includes('SearchFilesAsync')) {
  fail('SimpleFile.Ipc must still expose list_directory and search_files clients.');
}

const featureOpenLines = gate
  .split(/\r?\n/)
  .filter((line) => line.includes('| `OPEN` |') && !line.includes('Missing or only partial'));
const retirement = gate.slice(gate.indexOf('## Retirement lock'));
if (!retirement.includes('Required `OPEN` rows: **none**')) {
  fail('Retirement lock must state that required OPEN rows are none.');
}
if (featureOpenLines.length > 0) {
  fail(`parity-gate.md still has required OPEN row(s): ${featureOpenLines[0]}`);
}

if (!process.exitCode) {
  console.log(
    `WinUI parity gate lists ${commands.length} commands, ${ctxIds.length} context ids, ${paletteIds.length} palette ids.`,
  );
}
