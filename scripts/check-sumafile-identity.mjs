import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');

const staleNeedles = [
  'github.com/conniecombs/SimpleFile-Windows',
  'conniecombs/SimpleFile-Windows',
  'SimpleFile-Windows',
];

const allowedHistoricalPaths = [
  /^build_notes\//u,
  /^docs\/CHANGELOG\.md$/u,
  /^docs\/RELEASE_1\.1\.0\.md$/u,
  /^docs\/winui-migration\//u,
  /^scripts\/check-sumafile-identity\.mjs$/u,
];

function fail(message) {
  console.error(`SumaFile identity check failed: ${message}`);
  process.exitCode = 1;
}

function requireSnippet(source, file, snippet) {
  if (!source.includes(snippet)) {
    fail(`${file} must include ${snippet}.`);
  }
}

function readText(relativePath) {
  return fs.readFileSync(path.join(repoRoot, relativePath), 'utf8');
}

function extractStringConst(source, file, name) {
  const pattern = new RegExp(`(?:pub\\s+)?const\\s+${name}\\s*:\\s*&str\\s*=\\s*"([^"]+)"`, 'u');
  const match = pattern.exec(source);
  if (!match) {
    fail(`${file} must define ${name}.`);
    return '';
  }

  return match[1];
}

function isHistorical(relativePath) {
  return allowedHistoricalPaths.some((pattern) => pattern.test(relativePath));
}

const trackedFiles = execFileSync('git', ['ls-files'], {
  cwd: repoRoot,
  encoding: 'utf8',
})
  .split(/\r?\n/u)
  .filter(Boolean)
  .map((file) => file.replace(/\\/g, '/'));

for (const relativePath of trackedFiles) {
  if (isHistorical(relativePath)) {
    continue;
  }

  const absolutePath = path.join(repoRoot, relativePath);
  let text;
  try {
    text = fs.readFileSync(absolutePath, 'utf8');
  } catch {
    continue;
  }

  for (const needle of staleNeedles) {
    if (text.includes(needle)) {
      fail(`${relativePath} contains stale live repository identity: ${needle}`);
    }
  }
}

const readme = readText('README.md');
const packageJson = readText('package.json');
const release100 = readText('docs/RELEASE_1.0.0.md');
const upgradeFromRef = readText('scripts/smoke-winui-upgrade-from-ref.ps1');
const ipcLib = readText('crates/simplefile-ipc/src/lib.rs');
const settingsStore = readText('crates/simplefile-core/src/settings_store.rs');
const updater = readText('crates/simplefile-core/src/updater.rs');

for (const snippet of [
  'SumaFile 1.0.0',
  'docs/RELEASE_1.0.0.md',
  '## Known Limitations',
  'No manual import is needed for normal SimpleFile-to-SumaFile use.',
]) {
  requireSnippet(readme, 'README.md', snippet);
}

for (const snippet of [
  '# SumaFile 1.0.0 Release Checklist',
  'SumaFile_1.0.0_x64-winui-setup.exe',
  'SumaFile_1.0.0_x64-winui.msi',
  'SumaFile_1.0.0_x64-winui-portable.zip',
  'latest-winui.json',
  '## Dogfood 10-Step Script',
  'smoke:winui-upgrade-from-ref',
  '## SimpleFile Data Import',
  '## Known Limitations',
  'signed test release before claiming in-app updater installation is proven',
]) {
  requireSnippet(release100, 'docs/RELEASE_1.0.0.md', snippet);
}

requireSnippet(packageJson, 'package.json', 'smoke:winui-upgrade-from-ref');
for (const snippet of [
  'SUMAFILE_PREVIOUS_REF',
  'git @("worktree", "add", "--detach"',
  'build-winui-release.ps1',
  'smoke-winui-upgrade.ps1',
  'git @("worktree", "remove", "--force"',
]) {
  requireSnippet(upgradeFromRef, 'scripts/smoke-winui-upgrade-from-ref.ps1', snippet);
}

const appIdentifier = extractStringConst(ipcLib, 'crates/simplefile-ipc/src/lib.rs', 'APP_IDENTIFIER');
for (const [file, value] of [
  ['crates/simplefile-core/src/settings_store.rs', extractStringConst(settingsStore, 'crates/simplefile-core/src/settings_store.rs', 'APP_IDENTIFIER')],
  ['crates/simplefile-core/src/updater.rs', extractStringConst(updater, 'crates/simplefile-core/src/updater.rs', 'APP_IDENTIFIER')],
]) {
  if (value !== appIdentifier) {
    fail(`${file} APP_IDENTIFIER "${value}" must match simplefile-ipc "${appIdentifier}".`);
  }
}

if (!process.exitCode) {
  console.log('SumaFile live repository identity is wired.');
}
