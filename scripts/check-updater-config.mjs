import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');

function fail(message) {
  console.error(`Updater config check failed: ${message}`);
  process.exitCode = 1;
}

function readText(relativePath) {
  return fs.readFileSync(path.join(repoRoot, relativePath), 'utf8');
}

const writer = readText('scripts/write-latest-winui.mjs');
const releaseWorkflow = readText('.github/workflows/release.yml');
const props = readText('src-winui/Directory.Build.props');
const serviceCargo = readText('crates/simplefile-service/Cargo.toml');
const coreLib = readText('crates/simplefile-core/src/lib.rs');

const expectedEndpoint =
  'https://github.com/conniecombs/SimpleFile-Windows/releases/latest/download/';

if (!writer.includes('latest-winui.json')) {
  fail('scripts/write-latest-winui.mjs must write latest-winui.json.');
}
if (!writer.includes(expectedEndpoint)) {
  fail(`scripts/write-latest-winui.mjs must publish under ${expectedEndpoint}.`);
}
if (!writer.includes('windows-x86_64')) {
  fail('scripts/write-latest-winui.mjs must include the windows-x86_64 platform.');
}

const propsMatch = props.match(/<Version>([^<]+)<\/Version>/);
const displayMatch = props.match(/<InformationalVersion>([^<]+)<\/InformationalVersion>/);
const cargoMatch = serviceCargo.match(/^version\s*=\s*"([^"]+)"/m);
const displayConstMatch = coreLib.match(
  /pub const APP_DISPLAY_VERSION:\s*&str\s*=\s*"([^"]+)"/,
);
if (!propsMatch || !cargoMatch) {
  fail('Could not read versions from Directory.Build.props and simplefile-service Cargo.toml.');
} else if (propsMatch[1] !== cargoMatch[1]) {
  fail(
    `Version mismatch: Directory.Build.props=${propsMatch[1]} simplefile-service=${cargoMatch[1]}`,
  );
}
if (!displayMatch || !displayConstMatch) {
  fail('Could not read InformationalVersion and APP_DISPLAY_VERSION.');
} else if (displayMatch[1] !== displayConstMatch[1]) {
  fail(
    `Display version mismatch: InformationalVersion=${displayMatch[1]} APP_DISPLAY_VERSION=${displayConstMatch[1]}`,
  );
}

const requiredWorkflowSnippets = [
  'latest-winui.json',
  'x64-winui-setup.exe',
  'build-winui-release.ps1',
  'Directory.Build.props',
];

for (const snippet of requiredWorkflowSnippets) {
  if (!releaseWorkflow.includes(snippet)) {
    fail(`.github/workflows/release.yml must include ${snippet}.`);
  }
}

if (releaseWorkflow.includes('tauri.conf.json')) {
  fail('.github/workflows/release.yml should not still validate tauri.conf.json.');
}

if (!process.exitCode) {
  console.log(`Updater release configuration is enabled (WinUI ${displayMatch[1]}, numeric ${propsMatch[1]}).`);
}
