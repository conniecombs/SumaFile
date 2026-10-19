import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join, dirname, sep } from 'node:path';
import test from 'node:test';
import { generatedFiles } from '../lib/maintenance-policy.mjs';
import { verifyMaintenance } from '../verify-maintenance.mjs';

test('merge verification replays the real generators and rejects altered proposal content', () => {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-replay-'));
  const repositoryRoot = resolve(import.meta.dirname, '../..');
  const git = args => execFileSync('git', args, { encoding: 'utf8', cwd: root }).trim();
  try {
    for (const file of [...generatedFiles, 'scripts/generate-ipc-bindings.mjs', 'scripts/generate-open-with-policy.mjs',
      ...['commands', 'events', 'protocol', 'open-with-policy'].map(name => `ipc/schema/v1/${name}.json`)]) {
      mkdirSync(dirname(join(root, file)), { recursive: true });
      writeFileSync(join(root, file), readFileSync(join(repositoryRoot, file), 'utf8').replaceAll('\r\n', '\n'));
    }
    const target = generatedFiles[0];
    const expected = readFileSync(join(root, target), 'utf8');
    const stale = `${expected}// stale generated output\n`;
    writeFileSync(join(root, target), stale);
    git(['init', '--quiet']);
    git(['config', 'core.autocrlf', 'false']);
    git(['add', '.']);
    git(['-c', 'user.name=Maintenance fixture', '-c', 'user.email=fixture@example.test', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Fixture']);
    const source = git(['rev-parse', 'HEAD']);
    const input = { repository: 'owner/repo',
      commit: { commit: { message: `SumaFile-Maintenance: generated\nSumaFile-Source: ${source}` } },
      files: [{ filename: target, sha: 'blob', status: 'modified', additions: 0, deletions: 1 }],
      api: () => ({ encoding: 'base64', content: Buffer.from(expected).toString('base64') }),
    };
    assert.equal(verifyMaintenance(input, root), true);
    writeFileSync(join(root, target), stale);
    assert.equal(verifyMaintenance({ ...input, api: () => ({ encoding: 'base64', content: Buffer.from(expected + '// unexpected code').toString('base64') }) }, root), false);
    assert.equal(verifyMaintenance(input, root), false, 'dirty checkout must not be reused as trusted main');
  } finally {
    if (!resolve(root).startsWith(resolve(tmpdir()) + sep)) throw new Error('Unsafe fixture cleanup');
    rmSync(root, { recursive: true, force: true });
  }
});
