import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { extname, join, relative, resolve } from 'node:path';

const repoRoot = resolve(import.meta.dirname, '..');

const ignoredDirectories = new Set([
  '.git',
  'node_modules',
  'target',
  'dist',
  'build',
  'build_notes',
]);

const ignoredFiles = new Set([
  'Cargo.lock',
  'docs/CHANGELOG.md',
  'docs/changelog.md',
  'scripts/check-provider-surface.mjs',
]);

const ignoredExtensions = new Set([
  '.png',
  '.jpg',
  '.jpeg',
  '.ico',
  '.icns',
  '.gif',
  '.webp',
  '.bmp',
  '.zip',
  '.msi',
  '.exe',
]);

const bannedPatterns = [
  { label: 'Remote Drives', pattern: /\bRemote Drives\b/i },
  { label: 'remote-drives id/class', pattern: /\bremote-drives\b/i },
  { label: 'cloud command', pattern: /\bcloud_list_plugins\b/i },
  { label: 'Google OAuth env hook', pattern: /\bSIMPLEFILE_GOOGLE_CLIENT_ID\b/i },
  { label: 'Google OAuth credential file', pattern: /\bgoogle-oauth-client-id\.txt\b/i },
  { label: 'rclone command', pattern: /\brclone_[a-z0-9_]+\b/i },
  { label: 'rclone installer command', pattern: /\b(?:check|install)_rclone_installed\b|\binstall_rclone\b/i },
  { label: 'WinFsp installer command', pattern: /\b(?:check|install)_winfsp_installed\b|\binstall_winfsp\b/i },
  { label: 'rclone', pattern: /\brclone\b/i },
  { label: 'WinFsp', pattern: /\bWinFsp\b/i },
  { label: 'Google Drive', pattern: /\bGoogle Drive\b/i },
  { label: 'OneDrive', pattern: /\bOneDrive\b/i },
  { label: 'Dropbox', pattern: /\bDropbox\b/i },
  { label: 'pCloud', pattern: /\bpCloud\b/i },
  { label: 'S3-compatible', pattern: /\bS3-compatible\b/i },
  { label: 'WebDAV', pattern: /\bWebDAV\b/i },
  { label: 'provider ids', pattern: /\b(?:gdrive|onedrive|dropbox|pcloud|webdav)\b/i },
  { label: 'cloud wording', pattern: /\bcloud(?:-backed|-to-cloud)?\b/i },
];

const searchableExtensions = new Set([
  '.cjs',
  '.css',
  '.html',
  '.js',
  '.json',
  '.md',
  '.mjs',
  '.ps1',
  '.rs',
  '.toml',
  '.ts',
  '.txt',
  '.yml',
  '.yaml',
]);

const searchableFiles = new Set([
  '.gitignore',
]);

function toPosix(path) {
  return path.replaceAll('\\', '/');
}

function shouldSkip(relativePath) {
  const normalized = toPosix(relativePath);
  if (ignoredFiles.has(normalized)) return true;
  if (ignoredExtensions.has(extname(normalized).toLowerCase())) return true;
  return normalized.split('/').some((part) => ignoredDirectories.has(part));
}

function collectFiles(directory) {
  const entries = readdirSync(directory, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const absolute = join(directory, entry.name);
    const rel = toPosix(relative(repoRoot, absolute));
    if (shouldSkip(rel)) continue;

    if (entry.isDirectory()) {
      files.push(...collectFiles(absolute));
    } else if (
      entry.isFile()
      && (searchableExtensions.has(extname(entry.name).toLowerCase()) || searchableFiles.has(rel))
    ) {
      files.push(absolute);
    }
  }

  return files;
}

function lineAndColumn(source, index) {
  const before = source.slice(0, index);
  const lines = before.split(/\r?\n/);
  return {
    line: lines.length,
    column: lines.at(-1).length + 1,
  };
}

const findings = [];

for (const file of collectFiles(repoRoot)) {
  if (statSync(file).size > 2_000_000) continue;
  const source = readFileSync(file, 'utf8');
  const rel = toPosix(relative(repoRoot, file));

  for (const { label, pattern } of bannedPatterns) {
    pattern.lastIndex = 0;
    const match = pattern.exec(source);
    if (!match) continue;
    const location = lineAndColumn(source, match.index);
    findings.push(`${rel}:${location.line}:${location.column} ${label}: ${match[0]}`);
  }
}

if (findings.length > 0) {
  console.error('No-cloud surface check failed. Remove current-facing cloud/remote-drive references:');
  for (const finding of findings) {
    console.error(`- ${finding}`);
  }
  process.exit(1);
}

console.log('No cloud or remote-drive surface found.');
