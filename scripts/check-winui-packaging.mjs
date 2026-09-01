import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');

function fail(message) {
  console.error(`WinUI packaging check failed: ${message}`);
  process.exitCode = 1;
}

function readText(relativePath) {
  const fullPath = path.join(repoRoot, relativePath);
  if (!fs.existsSync(fullPath)) {
    fail(`missing ${relativePath}`);
    return '';
  }
  return fs.readFileSync(fullPath, 'utf8');
}

function requireSnippet(source, file, snippet) {
  if (!source.includes(snippet)) {
    fail(`${file} must include ${snippet}.`);
  }
}

const requiredFiles = [
  'packaging/winui/simplefile-winui.nsi',
  'packaging/winui/Product.wxs',
  'scripts/build-winui-release.ps1',
  'scripts/write-latest-winui.mjs',
  'scripts/smoke-winui-startup.ps1',
  'scripts/smoke-winui-file-ops.ps1',
  'scripts/smoke-winui-msi.ps1',
  'scripts/smoke-winui-nsis.ps1',
  'scripts/smoke-winui-upgrade.ps1',
  'scripts/generate-winui-icon.py',
  'scripts/check-winui-parity-gate.mjs',
  'docs/winui-migration/parity-gate.md',
  'src-winui/SimpleFile.App/SimpleFile.App.csproj',
  'crates/simplefile-service/Cargo.toml',
];

for (const relativePath of requiredFiles) {
  if (!fs.existsSync(path.join(repoRoot, relativePath))) {
    fail(`missing ${relativePath}`);
  }
}

const nsis = readText('packaging/winui/simplefile-winui.nsi');
const wxs = readText('packaging/winui/Product.wxs');
const buildScript = readText('scripts/build-winui-release.ps1');
const packageJson = readText('package.json');
const releaseYml = readText('.github/workflows/release.yml');
const ciYml = readText('.github/workflows/ci.yml');
const releaseBuildYml = readText('.github/workflows/release-build.yml');
const installerSmokeYml = readText('.github/workflows/installer-smoke.yml');
const appCsproj = readText('src-winui/SimpleFile.App/SimpleFile.App.csproj');

const nsisSnippets = [
  'Name "SumaFile"',
  'simplefile-service.exe',
  'SumaFile.exe',
  'InstallDir "$LOCALAPPDATA\\Programs\\SumaFile-WinUI"',
  'RequestExecutionLevel user',
  'QuietUninstallString',
];

for (const snippet of nsisSnippets) {
  requireSnippet(nsis, 'packaging/winui/simplefile-winui.nsi', snippet);
}

const wxsSnippets = [
  'Name="SumaFile"',
  'InstallScope="perUser"',
  'SimpleFileWinUIFiles',
  'SumaFile.exe',
];

for (const snippet of wxsSnippets) {
  requireSnippet(wxs, 'packaging/winui/Product.wxs', snippet);
}

const buildSnippets = [
  'simplefile-service',
  'dotnet publish',
  'SumaFile.exe',
  'x64-winui-portable.zip',
  'x64-winui-setup.exe',
  'x64-winui.msi',
  'latest-winui.json',
  'resources.pri',
  'MainWindow.xbf',
  '-sice:ICE03',
  '-sice:ICE38',
  '-sice:ICE64',
  '-sice:ICE91',
];

for (const snippet of buildSnippets) {
  requireSnippet(buildScript, 'scripts/build-winui-release.ps1', snippet);
}

const npmSnippets = [
  '"build:winui:release"',
  '"smoke:winui"',
  '"smoke:winui-file-ops"',
  '"smoke:winui-msi"',
  '"smoke:winui-installer"',
  '"smoke:winui-upgrade"',
  '"release:build"',
  '"dev:winui"',
];

for (const snippet of npmSnippets) {
  requireSnippet(packageJson, 'package.json', snippet);
}



requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'CopyWindowsAppSdkMergedPri');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'PublishUnpackagedXamlPayload');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', '<ApplicationIcon>..\\..\\packaging\\winui\\icon.ico</ApplicationIcon>');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', '<AssemblyName>SumaFile</AssemblyName>');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'SumaFile.png');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'SumaFile.ico');

const workflowSnippets = [
  ['ci.yml', ciYml, 'setup-dotnet'],
  ['ci.yml', ciYml, 'check:winui'],
  ['ci.yml', ciYml, 'simplefile-service'],
  ['release.yml', releaseYml, 'build-winui-release.ps1'],
  ['release.yml', releaseYml, 'latest-winui.json'],
  ['release.yml', releaseYml, 'SumaFile_*_x64-winui-portable.zip'],
  ['release.yml', releaseYml, 'x64-winui-portable.zip'],
  ['release.yml', releaseYml, 'build-winui-release.ps1'],
  ['release.yml', releaseYml, 'RequireUpdaterSignature'],
  ['release.yml', releaseYml, 'SIMPLEFILE_UPDATER_PUBLIC_KEY'],
  ['release.yml', releaseYml, 'smoke:winui-upgrade'],
  ['release.yml', releaseYml, 'smoke:winui-file-ops'],
  ['release-build.yml', releaseBuildYml, 'dist/winui'],
  ['release-build.yml', releaseBuildYml, 'SumaFile_*_x64-winui-portable.zip'],
  ['release-build.yml', releaseBuildYml, 'smoke:winui-file-ops'],
  ['release-build.yml', releaseBuildYml, 'smoke:winui-upgrade'],
  ['installer-smoke.yml', installerSmokeYml, 'smoke:winui'],
  ['installer-smoke.yml', installerSmokeYml, 'smoke:winui-file-ops'],
  ['installer-smoke.yml', installerSmokeYml, 'SumaFile_*_x64-winui-portable.zip'],
  ['installer-smoke.yml', installerSmokeYml, 'smoke:winui-upgrade'],
];

for (const [file, source, snippet] of workflowSnippets) {
  requireSnippet(source, file, snippet);
}

if (packageJson.includes('build:tauri') || packageJson.includes('smoke:release')) {
  fail('package.json still exposes retired Tauri package scripts.');
}

if (!process.exitCode) {
  console.log('WinUI packaging surface is wired.');
}
