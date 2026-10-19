import assert from 'node:assert/strict';
import test from 'node:test';
import { toolchainUpdates } from '../lib/toolchains.mjs';
const current = { rust: '1.98.0', dotnet: '10.0.402', node: '24.20.0' };
const feed = { rustManifest: '[pkg.rust]\nversion = "1.99.0 (commit)"', dotnetRelease: { 'latest-sdk': '10.0.403' },
  nodeReleases: [{ version: 'v26.0.0', lts: false }, { version: 'v24.21.0', lts: 'LTS' }] };
test('toolchain updates use stable feeds and preserve Node/.NET major versions', () => {
  assert.deepEqual(toolchainUpdates(current, feed), { rust: '1.99.0', dotnet: '10.0.403', node: '24.21.0' });
});
test('toolchain updates reject malformed feeds and never downgrade', () => {
  assert.throws(() => toolchainUpdates(current, { ...feed, rustManifest: 'invalid' }));
  assert.throws(() => toolchainUpdates(current, { ...feed, dotnetRelease: { 'latest-sdk': '11.0.100' } }));
  assert.deepEqual(toolchainUpdates({ rust: '1.99.0', dotnet: '10.0.403', node: '24.21.0' }, feed), {});
});
