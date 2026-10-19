import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runRecipe } from './lib/repair.mjs';
import { maintenanceMetadata, validateRepairFiles } from './lib/maintenance-policy.mjs';

const normalize = text => text.replaceAll('\r\n', '\n');

export function verifyMaintenance({ commit, files, api, repository }, root = process.cwd()) {
  const git = args => execFileSync('git', args, { encoding: 'utf8', cwd: root }).trim();
  const metadata = maintenanceMetadata(commit.commit.message);
  if (!metadata || metadata.recipe === 'release' || !validateRepairFiles(metadata.recipe, files)
      || git(['rev-parse', 'HEAD']) !== metadata.source || git(['status', '--porcelain'])) return false;
  // Replay from trusted main, using fixed commands. No PR code is checked out.
  // A moving toolchain feed can make verification differ: that safely requires
  // a fresh proposal instead of merging a stale version automatically.
  runRecipe(metadata.recipe, undefined, root);
  const changed = git(['diff', '--name-only']).split('\n').filter(Boolean).sort();
  if (JSON.stringify(changed) !== JSON.stringify(files.map(file => file.filename).sort())) return false;
  for (const file of files) {
    const blob = api(`repos/${repository}/git/blobs/${file.sha}`);
    if (blob.encoding !== 'base64' || normalize(Buffer.from(blob.content, 'base64').toString('utf8')) !== normalize(readFileSync(resolve(root, file.filename), 'utf8'))) return false;
  }
  return true;
}
