import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const cargoBin = 'cargo';

const ignoredAdvisories = [
  'RUSTSEC-2024-0370',
  'RUSTSEC-2024-0411',
  'RUSTSEC-2024-0412',
  'RUSTSEC-2024-0413',
  'RUSTSEC-2024-0414',
  'RUSTSEC-2024-0415',
  'RUSTSEC-2024-0416',
  'RUSTSEC-2024-0417',
  'RUSTSEC-2024-0418',
  'RUSTSEC-2024-0419',
  'RUSTSEC-2024-0420',
  'RUSTSEC-2024-0429',
  'RUSTSEC-2024-0436',
  'RUSTSEC-2025-0075',
  'RUSTSEC-2025-0080',
  'RUSTSEC-2025-0081',
  'RUSTSEC-2025-0098',
  'RUSTSEC-2025-0100',
  'RUSTSEC-2026-0190',
];

const args = ['audit', '--deny', 'warnings'];
for (const advisory of ignoredAdvisories) {
  args.push('--ignore', advisory);
}

const result = spawnSync(cargoBin, args, {
  cwd: rootDir,
  shell: process.platform === 'win32',
  stdio: 'inherit',
});

if (result.error) {
  console.error(result.error.message);
}

process.exit(result.status ?? 1);
