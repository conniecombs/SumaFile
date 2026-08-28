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

const version = named.version;
const setupName = named.setup;
const outDir = named.out ? path.resolve(named.out) : path.join(repoRoot, 'dist', 'winui');
const signature = named.signature ?? '';
const notes = named.notes ?? 'SumaFile WinUI 3 host + Rust IPC service.';

if (!version) {
  fail('Pass --version=BETA');
}
if (!setupName) {
  fail('Pass --setup=SumaFile_BETA_x64-winui-setup.exe');
}

const payload = {
  version,
  notes,
  pub_date: new Date().toISOString(),
  platforms: {
    'windows-x86_64': {
      signature,
      url: `https://github.com/conniecombs/SimpleFile-Windows/releases/latest/download/${setupName}`,
    },
  },
};

fs.mkdirSync(outDir, { recursive: true });
const outPath = path.join(outDir, 'latest-winui.json');
fs.writeFileSync(outPath, `${JSON.stringify(payload, null, 2)}\n`, 'utf8');
console.log(`Wrote ${outPath}`);
