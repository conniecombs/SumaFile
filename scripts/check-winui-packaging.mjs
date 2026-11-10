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
  'packaging/winui/sumafile-winui.nsi',
  'packaging/winui/Product.wxs',
  'scripts/build-winui-release.ps1',
  'scripts/write-latest-winui.mjs',
  'scripts/smoke-winui-startup.ps1',
  'scripts/smoke-winui-file-ops.ps1',
  'scripts/smoke-winui-msi.ps1',
  'scripts/smoke-winui-nsis.ps1',
  'scripts/smoke-winui-upgrade.ps1',
  'scripts/smoke-winui-msi-lifecycle.ps1',
  'scripts/lib/wix-msi.ps1',
  'scripts/generate-winui-icon.py',
  'scripts/check-winui-parity-gate.mjs',
  'docs/winui-migration/parity-gate.md',
  'src-winui/SimpleFile.App/SimpleFile.App.csproj',
  'src-winui/SimpleFile.PluginSdk/SimpleFile.PluginSdk.csproj',
  'src-winui/SimpleFile.PluginHost/SimpleFile.PluginHost.csproj',
  'crates/simplefile-service/Cargo.toml',
  'crates/sumafile-plugin/Cargo.toml',
  'third_party/7zip/win-x64/7za.exe',
  'third_party/7zip/win-x64/7za.dll',
  'third_party/7zip/win-x64/7zxa.dll',
  'third_party/7zip/win-x64/License.txt',
  'scripts/build-plugins.ps1',
  'scripts/lib/plugin-staging.ps1',
  'scripts/test-plugin-staging.ps1',
  'plugins/sevenzip/plugin.json',
  'plugins/sevenzip/backend/Cargo.toml',
  'plugins/remote/plugin.json',
  'plugins/remote/protocol.json',
  'plugins/remote/backend/Cargo.toml',
  'plugins/remote/test-server/Cargo.toml',
  'plugins/remote/ui/SumaFile.Remote/SumaFile.Remote.csproj',
  'plugins/remote/ui/SumaFile.Remote.Model/SumaFile.Remote.Model.csproj',
  'plugins/git/protocol.json',
  'plugins/git/backend/Cargo.toml',
];

for (const relativePath of requiredFiles) {
  if (!fs.existsSync(path.join(repoRoot, relativePath))) {
    fail(`missing ${relativePath}`);
  }
}

const nsis = readText('packaging/winui/sumafile-winui.nsi');
const wxs = readText('packaging/winui/Product.wxs');
const buildScript = readText('scripts/build-winui-release.ps1');
const packageJson = readText('package.json');
const releaseYml = readText('.github/workflows/release.yml');
const ciYml = readText('.github/workflows/ci.yml');
const releaseBuildYml = readText('.github/workflows/release-build.yml');
const installerSmokeYml = readText('.github/workflows/installer-smoke.yml');
const appCsproj = readText('src-winui/SimpleFile.App/SimpleFile.App.csproj');
const pluginStaging = readText('scripts/lib/plugin-staging.ps1');

let packageScripts = {};
try {
  packageScripts = JSON.parse(packageJson).scripts ?? {};
} catch (error) {
  fail(`package.json must be valid JSON: ${error.message}`);
}

const nsisSnippets = [
  'Name "SumaFile"',
  'simplefile-service.exe',
  'SumaFile.exe',
  'InstallDir "$LOCALAPPDATA\\Programs\\SumaFile-WinUI"',
  'RequestExecutionLevel user',
  'QuietUninstallString',
  'taskkill /F /IM sumafile-sevenzip-backend.exe',
  'RMDir /r "$INSTDIR\\tools\\7zip"',
  '$INSTDIR\\plugins\\com.sumafile.sevenzip',
  'taskkill /F /IM sumafile-remote-backend.exe',
  '$INSTDIR\\plugins\\com.sumafile.remote',
  // A reinstall or upgrade without /D= goes to the folder the last install used, so the user's plugins, plugin data,
  // approvals and plugin log in that folder are kept.
  'InstallDirRegKey HKCU',
];

for (const snippet of nsisSnippets) {
  requireSnippet(nsis, 'packaging/winui/sumafile-winui.nsi', snippet);
}

// The installer replaces each bundled plugin folder, never the whole plugins root or plugins-data: those hold the
// user's plugins and every plugin's data (phase 4). The closing quote right after the folder keeps the per-plugin
// lines from matching. "never touched" was the pre-phase-4 promise about a %LOCALAPPDATA% user root.
const forbiddenNsisSnippets = [
  'never touched',
  'RMDir /r "$INSTDIR\\plugins"',
  'RMDir /r "$INSTDIR\\plugins-data"',
];

for (const snippet of forbiddenNsisSnippets) {
  if (nsis.includes(snippet)) {
    fail(`packaging/winui/sumafile-winui.nsi must not contain ${snippet}; user plugins and plugin data in $INSTDIR survive a reinstall or upgrade.`);
  }
}

const wxsSnippets = [
  'Name="SumaFile"',
  'InstallScope="perUser"',
  'SimpleFileWinUIFiles',
  'SumaFile.exe',
  // The release identity; only the lifecycle smoke's build defines LifecycleSmoke and gets its own.
  'UpgradeCode = "8F3C1B6A-4D2E-4E91-9C77-2A6B5E1D0C44"',
  // A major upgrade installs into the remembered folder, and uninstall alone purges the four app-owned items there.
  'xmlns:util="http://schemas.microsoft.com/wix/UtilExtension"',
  'util:RemoveFolderEx',
  'NOT UPGRADINGPRODUCTCODE',
  'Name="InstallDir"',
  'SUMAFILE_PURGE_PLUGINS',
  'SUMAFILE_PURGE_DATA',
  'SumaFile-no-purge',
  'Name="plugins.log*"',
  'Name="plugin-approvals.json*"',
  // Removes the install folder once the purge emptied it; RemoveFolder never deletes a folder that holds anything.
  '<RemoveFolder Id="PurgeEmptyInstallDir" Property="SUMAFILE_PURGE_FILES" On="uninstall" />',
];

for (const snippet of wxsSnippets) {
  requireSnippet(wxs, 'packaging/winui/Product.wxs', snippet);
}

// The purge names the app-owned folders, never the install folder itself, which a custom install may share with
// the user's other files.
const removeFolderExElements = wxs.match(/<util:RemoveFolderEx\s[^>]*>/g) ?? [];
for (const element of removeFolderExElements) {
  if (element.includes('Property="SUMAFILE_PURGE_SOURCE"')) {
    fail('packaging/winui/Product.wxs must not purge Property="SUMAFILE_PURGE_SOURCE" with util:RemoveFolderEx; that is the whole install folder.');
  } else if (!/Property="SUMAFILE_PURGE_(PLUGINS|DATA)"/.test(element)) {
    fail(`packaging/winui/Product.wxs util:RemoveFolderEx may only purge SUMAFILE_PURGE_PLUGINS or SUMAFILE_PURGE_DATA: ${element}`);
  }
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
  // The absence check for 1.0.4's tools\7zip; stagingSnippets police the plugin folder's file list.
  'tools\\7zip',
  'sumafile-sevenzip-backend',
  'sumafile-remote-backend',
  'SumaFile.Remote.csproj',
  // The release pipeline stages the C# tests' fixtures (the Hello plugin, the SFTP test server) before check:winui.
  'build-fixture-plugin.ps1',
  'Install-BundledPlugins',
  'Assert-BundledPlugins',
  // The payload carries no app-owned plugin files (a user plugin, plugin data, approvals, the plugin log).
  'Get-AppOwnedPluginItems',
  // The MSI is built by scripts/lib/wix-msi.ps1, which the lifecycle smoke shares.
  'WixUtilExtension',
  'New-WinUIMsi',
];

for (const snippet of buildSnippets) {
  requireSnippet(buildScript, 'scripts/build-winui-release.ps1', snippet);
}

const wixMsiSnippets = [
  'function Resolve-WixBin',
  'function New-WinUIMsi',
  '-ext',
  'WixUtilExtension',
  '-sice:ICE03',
  '-sice:ICE38',
  '-sice:ICE64',
  '-sice:ICE91',
];

const wixMsi = readText('scripts/lib/wix-msi.ps1');
for (const snippet of wixMsiSnippets) {
  requireSnippet(wixMsi, 'scripts/lib/wix-msi.ps1', snippet);
}

const npmSnippets = [
  '"build:winui:release"',
  '"smoke:winui"',
  '"smoke:winui-file-ops"',
  '"smoke:winui-msi"',
  '"smoke:winui-installer"',
  '"smoke:winui-upgrade"',
  '"smoke:winui-msi-lifecycle"',
  '"release:build"',
  '"dev:winui"',
  '"build:plugins"',
];

for (const snippet of npmSnippets) {
  requireSnippet(packageJson, 'package.json', snippet);
}

const releaseBuildCommand = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-winui-release.ps1';
const expectedReleaseAliases = {
  build: 'npm run release:build',
  'build:winui:release': 'npm run release:build',
  'release:build': releaseBuildCommand,
  'release:local': 'npm run check:release && npm run release:build',
};

for (const [name, expectedCommand] of Object.entries(expectedReleaseAliases)) {
  if (packageScripts[name] !== expectedCommand) {
    fail(`package.json script ${name} must be "${expectedCommand}".`);
  }
}

if (Object.hasOwn(packageScripts, 'release:winui')) {
  fail('package.json should not keep the redundant release:winui alias; use release:build.');
}



requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'CopyWindowsAppSdkMergedPri');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'PublishUnpackagedXamlPayload');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', '<ApplicationIcon>..\\..\\packaging\\winui\\icon.ico</ApplicationIcon>');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', '<AssemblyName>SumaFile</AssemblyName>');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'SumaFile.png');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'SumaFile.ico');
requireSnippet(appCsproj, 'SimpleFile.App.csproj', 'CopyBundledPlugins');

// The staging library is what polices the bundled plugin folder's contents.
const stagingSnippets = [
  'third_party\\7zip\\win-x64',
  '7za.exe',
  '7za.dll',
  '7zxa.dll',
  'License.txt',
  'SUMAFILE_BUNDLE.md',
  'bundled.json',
  'com.sumafile.sevenzip',
  'sumafile-sevenzip-backend.exe',
  'com.sumafile.remote',
  'sumafile-remote-backend.exe',
  'SumaFile.Remote.dll',
  'SumaFile.Remote.Model.dll',
  'SumaFile.Remote.deps.json',
  'Assert-BundledPluginsFresh',
];

for (const snippet of stagingSnippets) {
  requireSnippet(pluginStaging, 'scripts/lib/plugin-staging.ps1', snippet);
}

const workflowSnippets = [
  ['ci.yml', ciYml, 'setup-dotnet'],
  ['ci.yml', ciYml, 'check:winui'],
  ['ci.yml', ciYml, 'simplefile-service'],
  ['ci.yml', ciYml, 'build-plugins.ps1'],
  ['ci.yml', ciYml, 'test-plugin-staging.ps1'],
  ['ci.yml', ciYml, 'check:winui-startup-budget'],
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
  ['installer-smoke.yml', installerSmokeYml, 'smoke:winui-msi-lifecycle'],
  ['release-build.yml', releaseBuildYml, 'smoke:winui-msi-lifecycle'],
  ['release.yml', releaseYml, 'smoke:winui-msi-lifecycle'],
];

for (const [file, source, snippet] of workflowSnippets) {
  requireSnippet(source, file, snippet);
}

// Plugin files live in the installation folder (phase 4). The smokes that launch the app point the plugin log at
// their disposable folder, so nothing is written into the payload or the Debug output.
const pluginLogSmokes = [
  'scripts/smoke-winui-startup.ps1',
  'scripts/smoke-winui-ui.ps1',
  'scripts/smoke-winui-msi.ps1',
];

for (const smoke of pluginLogSmokes) {
  requireSnippet(readText(smoke), smoke, 'SUMAFILE_PLUGIN_LOG_PATH');
}

// The installer smokes install only into a disposable folder and never touch another install: they refuse while
// SumaFile runs or a per-user install exists, snapshot the default install folder, and launch only their own copy
// with the plugin overrides removed. None may fall back to the default install folder's executable.
const installerSmokes = [
  'scripts/smoke-winui-nsis.ps1',
  'scripts/smoke-winui-msi-lifecycle.ps1',
  'scripts/smoke-winui-upgrade.ps1',
];
const installerSmokeSnippets = [
  'Get-SumaFileUninstallEntries -Scope CurrentUser',
  'Assert-NoSumaFileProcess',
  'Get-FolderSnapshot',
  'Start-InstalledSumaFile',
];
const forbiddenInstallerSmokeSnippets = ['Programs\\SumaFile-WinUI\\SumaFile.exe'];

for (const smoke of installerSmokes) {
  const source = readText(smoke);
  for (const snippet of installerSmokeSnippets) {
    requireSnippet(source, smoke, snippet);
  }
  const normalized = source.replace(/\\\\|\//g, '\\').toLowerCase();
  for (const snippet of forbiddenInstallerSmokeSnippets) {
    if (normalized.includes(snippet.toLowerCase())) {
      fail(`${smoke} must not launch ${snippet}; installer smokes run only the copy they installed.`);
    }
  }
}

// No script reads or writes the pre-release plugin locations under %LOCALAPPDATA%\SumaFile again. Escaped
// backslashes and forward slashes count too. Only this file names them; the staging library and its test build
// these paths from segments.
const forbiddenScriptSnippets = [
  'SumaFile\\plugins.log',
  'SumaFile\\plugins-data',
  'SumaFile\\plugin-approvals.json',
];
const forbiddenScanExtensions = new Set(['.ps1', '.mjs', '.js']);
const selfPath = path.join('scripts', 'check-winui-packaging.mjs');

function scriptFiles(relativeDir) {
  const files = [];
  for (const entry of fs.readdirSync(path.join(repoRoot, relativeDir), { withFileTypes: true })) {
    const relative = path.join(relativeDir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'node_modules') files.push(...scriptFiles(relative));
    } else if (forbiddenScanExtensions.has(path.extname(entry.name).toLowerCase())) {
      files.push(relative);
    }
  }
  return files;
}

for (const file of scriptFiles('scripts')) {
  if (file === selfPath) continue;
  const normalized = fs.readFileSync(path.join(repoRoot, file), 'utf8').replace(/\\\\|\//g, '\\');
  for (const snippet of forbiddenScriptSnippets) {
    if (normalized.toLowerCase().includes(snippet.toLowerCase())) {
      fail(`${file.split(path.sep).join('/')} must not reference the pre-release plugin location ${snippet}; plugin files live in the installation folder.`);
    }
  }
}

// Spec §12: plugin crates and the shared leaf crate they use never depend on the
// host crates. Later plugin crates join this list.
const pluginCrateManifests = [
  'crates/sumafile-transfer/Cargo.toml',
  'plugins/sevenzip/backend/Cargo.toml',
  'plugins/remote/backend/Cargo.toml',
  'plugins/remote/test-server/Cargo.toml',
  'plugins/git/backend/Cargo.toml',
];

for (const manifest of pluginCrateManifests) {
  const source = readText(manifest);
  if (source.includes('simplefile-core') || source.includes('simplefile-service')) {
    fail(`${manifest} must not depend on simplefile-core or simplefile-service (spec §12)`);
  }
}

if (packageJson.includes('build:tauri') || packageJson.includes('smoke:release')) {
  fail('package.json still exposes retired Tauri package scripts.');
}

if (!process.exitCode) {
  console.log('WinUI packaging surface is wired.');
}
