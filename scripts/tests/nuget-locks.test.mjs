import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import test from 'node:test';
import { applyProposal, dependabotProposal, restoreArgs, sameApartFromPackageVersions } from '../lib/nuget-locks.mjs';

const sha = 'c'.repeat(40);
const mainCsproj = '<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n    <PackageReference Include="Markdig" Version="1.3.2" />\n    <PackageReference Include="System.Data.OleDb" Version="9.0.6" />\n  </ItemGroup>\n</Project>\n';
const bumped = mainCsproj.replace('1.3.2', '1.6.0').replace('9.0.6', '9.0.23');

function fakeApi({ login = 'dependabot[bot]', headSha = sha, files, contents = {} } = {}) {
  const asked = [];
  const api = path => {
    asked.push(path);
    if (path === 'repos/owner/repo/pulls/80') return { number: 80, state: 'open', user: { login }, base: { ref: 'main' },
      head: { sha: headSha, repo: { full_name: 'owner/repo' } } };
    if (path.startsWith('repos/owner/repo/pulls/80/files')) return files ?? [
      { filename: 'src-winui/SimpleFile.Core/SimpleFile.Core.csproj', status: 'modified', patch: '@@ -3,2 +3,2 @@\n-    <PackageReference Include="Markdig" Version="1.3.2" />\n+    <PackageReference Include="Markdig" Version="1.6.0" />\n-    <PackageReference Include="System.Data.OleDb" Version="9.0.6" />\n+    <PackageReference Include="System.Data.OleDb" Version="9.0.23" />' },
      { filename: 'src-winui/SimpleFile.Core/packages.lock.json', status: 'modified', patch: '@@ -1 +1 @@\n-a\n+b' },
    ];
    const match = /^repos\/owner\/repo\/contents\/(.+)\?ref=(\w+)$/.exec(path);
    if (match) return { encoding: 'base64', content: Buffer.from(contents[match[1]] ?? bumped).toString('base64') };
    throw new Error(`unexpected ${path}`);
  };
  return { api, asked };
}

test('a proposal is the Dependabot pull request\'s project files, read at the head it names', () => {
  const { api, asked } = fakeApi();
  const proposal = dependabotProposal({ api, repository: 'owner/repo', number: 80, sha });
  assert.deepEqual(proposal, { number: 80, sha, csproj: [{ path: 'src-winui/SimpleFile.Core/SimpleFile.Core.csproj', content: bumped }] });
  assert.ok(asked.includes(`repos/owner/repo/contents/src-winui/SimpleFile.Core/SimpleFile.Core.csproj?ref=${sha}`));
});
test('a proposal is refused unless Dependabot wrote it, at that head, changing only package versions', () => {
  assert.throws(() => dependabotProposal({ api: fakeApi({ login: 'someone' }).api, repository: 'owner/repo', number: 80, sha }), /Dependabot/);
  assert.throws(() => dependabotProposal({ api: fakeApi({ headSha: 'd'.repeat(40) }).api, repository: 'owner/repo', number: 80, sha }), /head/);
  const files = [{ filename: 'src-winui/SimpleFile.Core/SimpleFile.Core.csproj', status: 'modified',
    patch: '@@ -1 +1 @@\n-    <PackageReference Include="Markdig" Version="1.3.2" />\n+    <PackageReference Include="Evil" Version="1.0.0" />' }];
  assert.throws(() => dependabotProposal({ api: fakeApi({ files }).api, repository: 'owner/repo', number: 80, sha }), /package versions/);
  const extra = [{ filename: 'src-winui/SimpleFile.App/MainWindow.xaml.cs', status: 'modified', patch: '@@ -1 +1 @@\n-a\n+b' }];
  assert.throws(() => dependabotProposal({ api: fakeApi({ files: extra }).api, repository: 'owner/repo', number: 80, sha }), /package versions/);
});
test('applying a proposal rewrites project files that differ from main only in package versions', () => {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-locks-'));
  try {
    const path = 'src-winui/SimpleFile.Core/SimpleFile.Core.csproj';
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), mainCsproj);
    assert.deepEqual(applyProposal(root, { number: 80, sha, csproj: [{ path, content: bumped }] }), [path]);
    assert.equal(readFileSync(join(root, path), 'utf8'), bumped);
    // Anything but a version change is refused, and the file is left alone.
    const tampered = bumped.replace('<Project Sdk="Microsoft.NET.Sdk">', '<Project Sdk="Microsoft.NET.Sdk">\n  <Target Name="x" />');
    assert.throws(() => applyProposal(root, { number: 80, sha, csproj: [{ path, content: tampered }] }), /package versions/);
    assert.equal(readFileSync(join(root, path), 'utf8'), bumped);
    assert.throws(() => applyProposal(root, { number: 80, sha, csproj: [{ path: 'src-winui/Missing/Missing.csproj', content: bumped }] }), /ENOENT|no such/i);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
test('project files compare equal apart from package versions and line endings', () => {
  assert.equal(sameApartFromPackageVersions(mainCsproj, bumped), true);
  assert.equal(sameApartFromPackageVersions(mainCsproj, bumped.replaceAll('\n', '\r\n')), true);
  assert.equal(sameApartFromPackageVersions(mainCsproj, bumped.replace('Markdig', 'Markdown')), false);
  assert.equal(sameApartFromPackageVersions(mainCsproj, bumped + '<!-- -->'), false);
});
test('the restore is the solution-wide forced evaluation CI restores in locked mode', () => {
  assert.deepEqual(restoreArgs(), ['restore', 'src-winui/SimpleFile.sln', '--force-evaluate', '-p:EnableWindowsTargeting=true']);
});
