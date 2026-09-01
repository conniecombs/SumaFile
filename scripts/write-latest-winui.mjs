import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');

function fail(message) {
  console.error(`latest-winui.json failed: ${message}`);
  process.exit(1);
}

const args = process.argv.slice(2);
const named = Object.fromEntries(
  args
    .filter((arg) => arg.startsWith('--') && arg.includes('='))
    .map((arg) => {
      const trimmed = arg.slice(2);
      const split = trimmed.indexOf('=');
      return [trimmed.slice(0, split), trimmed.slice(split + 1)];
    }),
);

const version = named.version?.trim();
const setupName = named.setup?.trim();
const outDir = named.out ? path.resolve(named.out) : path.join(repoRoot, 'dist', 'winui');
const signature = (named.signature ?? '').trim();
const sha256 = (named.sha256 ?? '').trim().toLowerCase();
const size = named.size ? Number.parseInt(named.size, 10) : 0;
const channel = (named.channel ?? 'stable').trim();
const requireInstallable = named['require-installable'] === 'true';
const notes = named.notes ?? 'SumaFile WinUI 3 host + Rust IPC service.';

if (!version) {
  fail('Pass --version=1.0.0');
}
if (!setupName) {
  fail('Pass --setup=SumaFile_1.0.0_x64-winui-setup.exe');
}
if (!/^SumaFile_[^/\\\s]+_x64-winui-setup\.exe$/u.test(setupName)) {
  fail('--setup must be a SumaFile x64 WinUI setup executable name.');
}
if (sha256 && !/^[a-f0-9]{64}$/iu.test(sha256)) {
  fail('--sha256 must be a 64-character hex string.');
}
if (named.size && (!Number.isSafeInteger(size) || size < 0)) {
  fail('--size must be a non-negative integer byte count.');
}
if (requireInstallable && (!signature || !sha256 || size <= 0)) {
  fail('Installable updater metadata requires --signature, --sha256, and --size.');
}

const payload = {
  version,
  notes,
  pub_date: new Date().toISOString(),
  channel,
  install_ready: Boolean(signature && sha256 && size > 0),
  platforms: {
    'windows-x86_64': {
      signature,
      sha256,
      size,
      url: `https://github.com/conniecombs/SumaFile/releases/latest/download/${setupName}`,
    },
  },
};

fs.mkdirSync(outDir, { recursive: true });
const outPath = path.join(outDir, 'latest-winui.json');
fs.writeFileSync(outPath, `${JSON.stringify(payload, null, 2)}\n`, 'utf8');
console.log(`Wrote ${outPath}`);
