import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, dirname, join } from 'node:path';
import test from 'node:test';
import { nugetLocks, parseVersion, planReleaseVersion, readReleaseVersion } from '../lib/release-version.mjs';

const sourceRoot = resolve(import.meta.dirname, '../..');
const files = [...nugetLocks, 'Cargo.lock', 'README.md', 'src-winui/Directory.Build.props', 'crates/simplefile-core/src/lib.rs',
  ...['simplefile-core', 'simplefile-ipc', 'simplefile-service'].map(crate => `crates/${crate}/Cargo.toml`)];
test('release preview is read-only and applying it keeps every identity in sync', () => {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-version-'));
  try {
    for (const file of files) {
      mkdirSync(dirname(resolve(root, file)), { recursive: true });
      writeFileSync(resolve(root, file), readFileSync(resolve(sourceRoot, file)));
    }
    const original = readReleaseVersion(root);
    const { changes } = planReleaseVersion(root, 'v1.2.3-beta.1');
    assert.deepEqual(readReleaseVersion(root), original);
    assert.equal(changes.size, 9);
    for (const [file, contents] of changes) writeFileSync(resolve(root, file), contents);
    assert.deepEqual(readReleaseVersion(root), { numeric: '1.2.3', display: '1.2.3-beta.1', tag: 'v1.2.3-beta.1', prerelease: true });
    const lock = readFileSync(resolve(root, 'Cargo.lock'), 'utf8');
    assert.match(lock, /name = "simplefile-core"\r?\nversion = "1.2.3"/);
    for (const file of nugetLocks) {
      const contents = readFileSync(resolve(root, file), 'utf8');
      for (const match of contents.matchAll(/"SimpleFile\.(?:Core|Ipc|App|Tests)": "\[([^,]+)/g)) assert.equal(match[1], '1.2.3');
    }
    const readme = readFileSync(resolve(root, 'README.md'), 'utf8');
    assert.match(readme, /version-1\.2\.3%2Dbeta\.1-2563eb/);
    assert.match(readme, /SumaFile_1\.2\.3-beta\.1_x64-winui/);
    assert.equal(planReleaseVersion(root, '1.2.3-beta.1').changes.size, 0);
  } finally {
    if (!resolve(root).startsWith(resolve(tmpdir()) + '\\') && !resolve(root).startsWith(resolve(tmpdir()) + '/')) throw new Error('Unsafe fixture cleanup');
    rmSync(root, { recursive: true });
  }
});
test('reject malformed or nonnumeric installer versions', () => {
  for (const value of ['1.2', '1.2.3;echo bad', '01.2.3', '256.0.0', '1.256.0', '1.0.65536', '1.2.3\nextra']) assert.throws(() => parseVersion(value));
});
