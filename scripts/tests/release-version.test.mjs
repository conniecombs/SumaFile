import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { findNugetLocks, parseVersion, planReleaseVersion, readReleaseVersion, readReleaseNotes } from '../lib/release-version.mjs';

const sourceRoot = resolve(import.meta.dirname, '../..');
const nugetLocks = findNugetLocks(sourceRoot);
const files = [...nugetLocks, 'Cargo.lock', 'crates/sumafile-plugin/Cargo.toml', 'README.md', 'src-winui/Directory.Build.props', 'crates/simplefile-core/src/lib.rs',
  'src-winui/SimpleFile.App/app.manifest', 'packaging/winui/sumafile-winui.nsi',
  ...['simplefile-core', 'simplefile-ipc', 'simplefile-service'].map(crate => `crates/${crate}/Cargo.toml`)];
// NuGet writes a project reference's version into the lock file of every project that reaches it ("SimpleFile.PluginSdk":
// "[1.0.4, )"), and a CI restore runs in locked mode, so a bump must rewrite them all.
test('every packages.lock.json is found, the plugin, template and fixture ones included', () => {
  for (const file of [
    'src-winui/SimpleFile.App/packages.lock.json',
    'src-winui/SimpleFile.Tests/packages.lock.json',
    'src-winui/SimpleFile.PluginHost/packages.lock.json',
    'src-winui/SimpleFile.PluginSdk/packages.lock.json',
    'src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/packages.lock.json',
    'plugins/git/ui/SumaFile.Git/packages.lock.json',
    'plugins/remote/ui/SumaFile.Remote/packages.lock.json',
    'templates/sumafile-plugin/ui/Example.Plugin/packages.lock.json',
  ]) assert.ok(nugetLocks.includes(file), `${file} is found`);
  assert.ok(nugetLocks.every(file => !/(^|\/)(bin|obj|node_modules|target)\//.test(file)), 'build output is skipped');
  // The lock files that name a src-winui project today; each one is rewritten by a bump.
  const named = nugetLocks.filter(file => /"SimpleFile\.[A-Za-z]+": "\[/.test(readFileSync(resolve(sourceRoot, file), 'utf8')));
  for (const file of ['src-winui/SimpleFile.App/packages.lock.json', 'src-winui/SimpleFile.Tests/packages.lock.json',
    'src-winui/SimpleFile.PluginHost/packages.lock.json', 'plugins/git/ui/SumaFile.Git/packages.lock.json',
    'plugins/remote/ui/SumaFile.Remote/packages.lock.json']) assert.ok(named.includes(file), `${file} names a SimpleFile project`);
});

test('release preview is read-only and applying it keeps every identity in sync', () => {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-version-'));
  try {
    for (const file of files) {
      mkdirSync(dirname(resolve(root, file)), { recursive: true });
      writeFileSync(resolve(root, file), readFileSync(resolve(sourceRoot, file)));
    }
    // A plugin project's own version (1.0.0, not the app's) is never rewritten.
    const gitLock = resolve(root, 'plugins/git/ui/SumaFile.Git/packages.lock.json');
    writeFileSync(gitLock, readFileSync(gitLock, 'utf8').replace('"SimpleFile.PluginSdk": "[1.0.4, )"',
      '"SimpleFile.PluginSdk": "[1.0.4, )",\n          "SumaFile.Git.Model": "[1.0.0, )"'));
    const original = readReleaseVersion(root);
    const { changes } = planReleaseVersion(root, 'v1.2.3-beta.1');
    assert.deepEqual(readReleaseVersion(root), original);
    const locksWithProjects = nugetLocks.filter(file => /"SimpleFile\.[A-Za-z]+": "\[/.test(readFileSync(resolve(root, file), 'utf8')));
    assert.deepEqual([...changes.keys()].sort(), [
      ...['simplefile-core', 'simplefile-ipc', 'simplefile-service', 'sumafile-plugin'].map(crate => `crates/${crate}/Cargo.toml`),
      'Cargo.lock', 'README.md', 'src-winui/Directory.Build.props', 'crates/simplefile-core/src/lib.rs',
      'src-winui/SimpleFile.App/app.manifest', 'packaging/winui/sumafile-winui.nsi', ...locksWithProjects,
    ].sort());
    for (const [file, contents] of changes) writeFileSync(resolve(root, file), contents);
    assert.deepEqual(readReleaseVersion(root), { numeric: '1.2.3', display: '1.2.3-beta.1', tag: 'v1.2.3-beta.1', prerelease: true });
    const lock = readFileSync(resolve(root, 'Cargo.lock'), 'utf8');
    assert.match(lock, /name = "simplefile-core"\r?\nversion = "1.2.3"/);
    let rewritten = 0;
    for (const file of nugetLocks) {
      const contents = readFileSync(resolve(root, file), 'utf8');
      for (const match of contents.matchAll(/"SimpleFile\.[A-Za-z]+": "\[([^,]+)/g)) {
        assert.equal(match[1], '1.2.3', `${file}: ${match[0]}`);
        rewritten += 1;
      }
    }
    assert.ok(rewritten >= 12, `${rewritten} project references rewritten`);
    assert.match(readFileSync(gitLock, 'utf8'), /"SumaFile\.Git\.Model": "\[1\.0\.0, \)"/);
    // The packaged sumafile-plugin requires the simplefile-ipc released with it; its own version is its own.
    const pluginCrate = readFileSync(resolve(root, 'crates/sumafile-plugin/Cargo.toml'), 'utf8');
    assert.match(pluginCrate, /^simplefile-ipc = \{ path = "\.\.\/simplefile-ipc", version = "1\.2\.3" \}\r?$/m);
    assert.match(pluginCrate, /^version = "1\.0\.0"\r?$/m);
    const readme = readFileSync(resolve(root, 'README.md'), 'utf8');
    assert.match(readme, /version-1\.2\.3%2Dbeta\.1-2563eb/);
    assert.match(readme, /SumaFile_1\.2\.3-beta\.1_x64-winui/);
    assert.equal(planReleaseVersion(root, '1.2.3-beta.1').changes.size, 0);
    const manifestPath = resolve(root, 'src-winui/SimpleFile.App/app.manifest');
    const manifest = readFileSync(manifestPath, 'utf8');
    writeFileSync(manifestPath, manifest.replace('1.2.3.0', '1.0.0.0'));
    assert.throws(() => readReleaseVersion(root), /Windows manifest version/);
    writeFileSync(manifestPath, manifest);
    const nsisPath = resolve(root, 'packaging/winui/sumafile-winui.nsi');
    const nsis = readFileSync(nsisPath, 'utf8');
    writeFileSync(nsisPath, nsis.replace('NUMERIC_VERSION "1.2.3"', 'NUMERIC_VERSION "1.0.0"'));
    assert.throws(() => readReleaseVersion(root), /NSIS fallback version/);
    writeFileSync(nsisPath, nsis);
    // A stale lock entry or plugin-crate requirement is caught too.
    const sdkLock = resolve(root, 'plugins/remote/ui/SumaFile.Remote/packages.lock.json');
    const sdk = readFileSync(sdkLock, 'utf8');
    writeFileSync(sdkLock, sdk.replace('"SimpleFile.PluginSdk": "[1.2.3, )"', '"SimpleFile.PluginSdk": "[1.0.4, )"'));
    assert.throws(() => readReleaseVersion(root), /SumaFile\.Remote\/packages\.lock\.json: SimpleFile\.PluginSdk is 1\.0\.4/);
    writeFileSync(sdkLock, sdk);
    const pluginPath = resolve(root, 'crates/sumafile-plugin/Cargo.toml');
    writeFileSync(pluginPath, pluginCrate.replace('version = "1.2.3" }', 'version = "1.0.4" }'));
    assert.throws(() => readReleaseVersion(root), /sumafile-plugin requires simplefile-ipc 1\.0\.4/);
    writeFileSync(pluginPath, pluginCrate);
    assert.deepEqual(readReleaseVersion(root).numeric, '1.2.3');
  } finally {
    if (!resolve(root).startsWith(resolve(tmpdir()) + '\\') && !resolve(root).startsWith(resolve(tmpdir()) + '/')) throw new Error('Unsafe fixture cleanup');
    rmSync(root, { recursive: true });
  }
});
test('reject malformed or nonnumeric installer versions', () => {
  for (const value of ['1.2', '1.2.3;echo bad', '01.2.3', '256.0.0', '1.256.0', '1.0.65536', '1.2.3\nextra']) assert.throws(() => parseVersion(value));
});

function resolveWorkflowRelease(overrides) {
  const result = spawnSync(process.execPath, [resolve(sourceRoot, 'scripts/release-version.mjs'), '--resolve'], {
    cwd: sourceRoot,
    encoding: 'utf8',
    env: {
      ...process.env,
      GITHUB_OUTPUT: '',
      RELEASE_VERSION: '',
      ...overrides,
    },
  });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

test('version-tag pushes publish after release gates pass', () => {
  const version = readReleaseVersion(sourceRoot);
  const resolved = resolveWorkflowRelease({
    GITHUB_EVENT_NAME: 'push',
    GITHUB_REF_TYPE: 'tag',
    GITHUB_REF_NAME: version.tag,
    RELEASE_DRAFT: '',
  });
  assert.equal(resolved.draft, 'false');
  assert.equal(resolved.notes, `docs/RELEASE_${version.display}.md`);
});

test('publication requires version-specific release notes with a matching heading', () => {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-notes-'));
  try {
    const version = parseVersion('1.2.3');
    assert.throws(() => readReleaseNotes(root, version), /ENOENT/);
    mkdirSync(join(root, 'docs'));
    const file = join(root, 'docs/RELEASE_1.2.3.md');
    writeFileSync(file, '# SumaFile 1.2.2 Release Notes\n');
    assert.throws(() => readReleaseNotes(root, version), /heading/);
    writeFileSync(file, '# SumaFile 1.2.3 Release Notes\n\nReviewed changes.\n');
    assert.match(readReleaseNotes(root, version).source, /Reviewed changes/);
  } finally { rmSync(root, { recursive: true }); }
});

test('manual releases remain drafts by default and can explicitly publish', () => {
  const manual = { GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_REF_TYPE: 'branch', GITHUB_REF_NAME: 'main' };
  assert.equal(resolveWorkflowRelease({ ...manual, RELEASE_DRAFT: '' }).draft, 'true');
  assert.equal(resolveWorkflowRelease({ ...manual, RELEASE_DRAFT: 'false' }).draft, 'false');
});
