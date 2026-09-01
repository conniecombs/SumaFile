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

if (!process.exitCode) {
  console.log('SumaFile live repository identity is wired.');
}
