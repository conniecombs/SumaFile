import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join, sep } from 'node:path';
import test from 'node:test';

test('provider guard accepts documentation vocabulary but rejects executable hooks and dependencies', () => {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-provider-'));
  try {
    mkdirSync(join(root, 'scripts')); mkdirSync(join(root, 'src'));
    const script = join(root, 'scripts/check-provider-surface.mjs');
    writeFileSync(script, readFileSync(new URL('../check-provider-surface.mjs', import.meta.url)));
    writeFileSync(join(root, 'FutureFeatures.md'), 'Cloud storage, Google Drive and Dropbox may be discussed here.\n');
    const run = () => spawnSync(process.execPath, [script], { encoding: 'utf8' });
    assert.equal(run().status, 0);
    writeFileSync(join(root, 'src/Bridge.cs'), 'var setting = "SIMPLEFILE_GOOGLE_CLIENT_ID";');
    assert.notEqual(run().status, 0);
    writeFileSync(join(root, 'src/Bridge.cs'), 'var cloudDescription = "Cloud backup notes";');
    assert.equal(run().status, 0);
    writeFileSync(join(root, 'src/Host.csproj'), '<PackageReference Include="dropbox-sdk" Version="1.0.0" />');
    assert.notEqual(run().status, 0);
  } finally {
    if (!resolve(root).startsWith(resolve(tmpdir()) + sep)) throw new Error('Unsafe fixture cleanup');
    rmSync(root, { recursive: true });
  }
});
