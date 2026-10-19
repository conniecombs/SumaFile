import assert from 'node:assert/strict';
import test from 'node:test';
import { evaluateMerge, requiredJobs, safeDependencyFile } from '../lib/merge-policy.mjs';

const fixture = () => ({
  repository: 'owner/repo',
  pr: { state: 'open', draft: false, base: { ref: 'main' }, head: { sha: 'abc', repo: { full_name: 'owner/repo' } },
    user: { login: 'dependabot[bot]' }, mergeable: true, mergeable_state: 'clean' },
  run: { path: '.github/workflows/ci.yml', name: 'CI', event: 'pull_request', status: 'completed', conclusion: 'success',
    head_sha: 'abc', head_repository: { full_name: 'owner/repo' } },
  jobs: requiredJobs.map(name => ({ name, conclusion: 'success' })),
  protection: { required_status_checks: { strict: true, contexts: ['CI required'] }, enforce_admins: { enabled: true } },
  commit: { sha: 'abc', author: { login: 'dependabot[bot]' }, commit: { message: 'Update\nupdated-dependencies:\n- dependency-name: test\n  update-type: version-update:semver-patch\n...\nSigned-off-by: bot' } },
  files: [{ filename: 'Cargo.lock', status: 'modified', patch: '-version = "1.0.0"\n+version = "1.0.1"' }],
});
test('eligible dependency head passes every required gate', () => assert.equal(evaluateMerge(fixture()).eligible, true));
for (const [name, change] of [
  ['stale successful run', input => { input.pr.head.sha = 'new'; }],
  ['missing gate', input => { input.jobs.pop(); }],
  ['skipped gate', input => { input.jobs[0].conclusion = 'skipped'; }],
  ['failed gate', input => { input.jobs[0].conclusion = 'failure'; }],
  ['duplicate gate', input => { input.jobs.push(input.jobs[0]); }],
  ['no enforced protection', input => { input.protection.enforce_admins.enabled = false; }],
  ['outdated base', input => { input.pr.mergeable_state = 'behind'; }],
  ['fork', input => { input.pr.head.repo.full_name = 'stranger/repo'; }],
  ['major update', input => { input.commit.commit.message = input.commit.commit.message.replace('semver-patch', 'semver-major'); }],
  ['human code on bot PR', input => { input.commit.author.login = 'human'; }],
  ['test deletion', input => { input.files = [{ filename: 'tests/security.rs', status: 'removed', patch: '-test' }]; }],
]) test(`merge refuses ${name}`, () => {
  const input = fixture(); change(input); assert.equal(evaluateMerge(input).eligible, false);
});
test('action ref upgrades pass but command changes and action substitution do not', () => {
  const file = { filename: '.github/workflows/ci.yml', status: 'modified', patch: '- uses: actions/checkout@v7\n+ uses: actions/checkout@v8' };
  assert.equal(safeDependencyFile(file), true);
  assert.equal(safeDependencyFile({ ...file, patch: file.patch + '\n+ run: exit 0' }), false);
  assert.equal(safeDependencyFile({ ...file, patch: file.patch.replace('checkout@v8', 'other@v8') }), false);
});
test('Cargo feature changes and NuGet package substitution require review', () => {
  assert.equal(safeDependencyFile({ filename: 'crates/core/Cargo.toml', status: 'modified', patch:
    '-zip = { version = "9", features = ["a"] }\n+zip = { version = "10", features = ["b"] }' }), false);
  assert.equal(safeDependencyFile({ filename: 'src-winui/App/App.csproj', status: 'modified', patch:
    '-<PackageReference Include="First" Version="1.0.0" />\n+<PackageReference Include="Other" Version="1.0.1" />' }), false);
});

function maintenanceFixture() {
  const input = fixture();
  const source = 'a'.repeat(40);
  input.pr.user.login = input.commit.author.login = 'github-actions[bot]';
  input.pr.base.sha = source;
  input.pr.head.ref = 'connie/maintenance/toolchains-aaaaaaaaaaaa-test';
  input.commit.parents = [{ sha: source }];
  input.commit.commit.message = `Maintain toolchains\nSumaFile-Maintenance: toolchains\nSumaFile-Source: ${source}`;
  input.files = [{ filename: '.node-version', status: 'modified', additions: 1, deletions: 1 }];
  input.run.event = 'workflow_dispatch';
  input.repairVerified = true;
  return input;
}
test('maintenance requires a reproduced recipe, current main parent and all gates', () => {
  assert.equal(evaluateMerge(maintenanceFixture()).eligible, true);
  for (const change of [input => { input.repairVerified = false; }, input => { input.pr.base.sha = 'new-main'; },
    input => { input.commit.parents.push({ sha: 'other' }); }, input => { input.files[0].filename = 'scripts/automerge-dependencies.mjs'; },
    input => { input.jobs.find(job => job.name.startsWith('Installer verification')).conclusion = 'skipped'; }]) {
    const input = maintenanceFixture(); change(input); assert.equal(evaluateMerge(input).eligible, false);
  }
});
test('release version changes always require human review', () => {
  const input = maintenanceFixture();
  input.commit.commit.message = input.commit.commit.message.replace('SumaFile-Maintenance: toolchains', 'SumaFile-Maintenance: release');
  assert.equal(evaluateMerge(input).eligible, false);
});
