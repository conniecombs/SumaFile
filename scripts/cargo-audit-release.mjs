import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const cargoBin = 'cargo';

import { auditArgs } from './lib/audit-policy.mjs';

const result = spawnSync(cargoBin, auditArgs, {
  cwd: rootDir,
  shell: process.platform === 'win32',
  stdio: 'inherit',
});

if (result.error) {
  console.error(result.error.message);
}

process.exit(result.status ?? 1);
