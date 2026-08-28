import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const repoRoot = resolve(import.meta.dirname, '..');

function fail(message) {
  console.error(`Windows asset check failed: ${message}`);
  process.exitCode = 1;
}

function assertMissing(relativePath) {
  if (existsSync(resolve(repoRoot, relativePath))) {
    fail(`${relativePath} should not be tracked in the Windows-only package surface.`);
  }
}

function assertExists(relativePath) {
  if (!existsSync(resolve(repoRoot, relativePath))) {
    fail(`${relativePath} must exist for WinUI packaging.`);
  }
}

for (const relativePath of [
  'src-tauri/icons/icon.icns',
  'src-tauri/icons/android',
  'src-tauri/icons/ios',
  'src-tauri/gen/schemas/linux-schema.json',
  'src-tauri/tauri.conf.json',
]) {
  assertMissing(relativePath);
}

for (const relativePath of [
  'base_icon.png',
  'packaging/winui/icon.ico',
  'packaging/winui/simplefile-winui.nsi',
  'packaging/winui/Product.wxs',
  'scripts/generate-winui-icon.py',
]) {
  assertExists(relativePath);
}

const nsis = readFileSync(resolve(repoRoot, 'packaging/winui/simplefile-winui.nsi'), 'utf8');
const wxs = readFileSync(resolve(repoRoot, 'packaging/winui/Product.wxs'), 'utf8');

if (!nsis.includes('Name "SumaFile"') || !nsis.includes('RequestExecutionLevel user')) {
  fail('packaging/winui/simplefile-winui.nsi must remain a per-user Windows NSIS installer.');
}
if (!wxs.includes('InstallScope="perUser"') || !wxs.includes('SimpleFileWinUIFiles')) {
  fail('packaging/winui/Product.wxs must remain a per-user Windows MSI.');
}

if (!process.exitCode) {
  console.log('Windows packaging assets are scoped to WinUI NSIS/MSI.');
}
