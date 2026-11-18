import assert from 'node:assert/strict';
import test from 'node:test';
import { classifyFailure, maintenanceMetadata, validateRepairFiles } from '../lib/maintenance-policy.mjs';
import { publishMaintenance } from '../lib/publish-maintenance.mjs';

const source = 'a'.repeat(40);
const fixture = (step = 'Check formatting', log = 'Diff in crates/simplefile-core/src/lib.rs:10:') => ({
  repository: 'owner/repo', mainSha: source, log,
  run: { name: 'CI', path: '.github/workflows/ci.yml', status: 'completed', conclusion: 'failure', run_attempt: 1,
    head_branch: 'main', head_sha: source, head_repository: { full_name: 'owner/repo' } },
  jobs: [{ name: 'Rust quality gate', conclusion: 'failure', steps: [{ name: step, conclusion: 'failure' }] },
    { name: 'CI required', conclusion: 'failure', steps: [{ name: 'aggregate', conclusion: 'failure' }] }],
});

test('known formatter and generator failures receive deterministic recipes', () => {
  assert.equal(classifyFailure(fixture()).recipe, 'rustfmt');
  assert.equal(classifyFailure(fixture('Run repo checks', 'Protocol.Generated.cs is stale. Run npm run generate:ipc-bindings.')).recipe, 'generated');
});
test('network retry is limited to infrastructure steps and one attempt', () => {
  const input = fixture('Setup Rust', 'ECONNRESET');
  assert.equal(classifyFailure(input).action, 'retry');
  input.run.run_attempt = 2;
  assert.equal(classifyFailure(input).action, 'review');
  assert.equal(classifyFailure(fixture('Run tests', 'ECONNRESET')).action, 'review');
  const stale = fixture('Setup Rust', 'ECONNRESET'); stale.branchHead = 'b'.repeat(40);
  assert.equal(classifyFailure(stale).action, 'review');
});
test('compiler and security evidence takes priority over incidental network messages', () => {
  for (const error of ['error[E0308]: mismatched types', 'error CS1001: Identifier expected', 'RUSTSEC-2026-0123', 'test result: FAILED']) {
    assert.equal(classifyFailure(fixture('Setup Rust', `ECONNRESET\n${error}`)).action, 'review');
  }
});
test('unknown, missing, stale and fork failures never cause speculative edits', () => {
  assert.equal(classifyFailure(fixture('Run tests', 'something happened')).action, 'review');
  assert.equal(classifyFailure(fixture('Setup Rust', '')).action, 'review');
  for (const change of [input => { input.run.head_sha = 'b'.repeat(40); }, input => { input.run.head_branch = 'dependabot/rust'; },
    input => { input.run.head_repository.full_name = 'fork/repo'; }]) {
    const input = fixture(); change(input); assert.equal(classifyFailure(input).action, 'review');
  }
});
test('comparison identifies matching failed steps on current main', () => {
  const input = fixture(); input.baseJobs = input.jobs;
  assert.equal(classifyFailure(input).baseline, 'main has the same failing steps');
});
test('repair bounds reject deletions, traversal, duplicate paths and large diffs', () => {
  const file = { filename: '.node-version', status: 'modified', additions: 1, deletions: 1 };
  assert.equal(validateRepairFiles('toolchains', [file]), true);
  for (const files of [[{ ...file, filename: '../.node-version' }], [{ ...file, status: 'removed' }],
    [file, file], [{ ...file, additions: 1501 }], [{ ...file, additions: NaN }], [{ ...file, filename: '.github/workflows/ci.yml' }]]) {
    assert.equal(validateRepairFiles('toolchains', files), false);
  }
});

function fakeApi({ moved = false, prior = [], existing = false } = {}) {
  const calls = [];
  const api = (path, options = {}) => {
    calls.push({ path, ...options });
    if (path.endsWith('/branches/main')) return { commit: { sha: moved ? 'b'.repeat(40) : source } };
    if (path.includes('/pulls?')) return prior;
    if (path.includes('/runs?')) return { workflow_runs: [] };
    if (path.endsWith(`/git/commits/${source}`)) return { tree: { sha: 'base-tree' } };
    if (path.endsWith('/git/commits/repair')) return { tree: { sha: 'new-tree' }, parents: [{ sha: source }] };
    if (path.endsWith('/git/trees')) return { sha: 'new-tree' };
    if (path.includes('/git/ref/heads/')) { if (existing) return { object: { sha: 'repair' } }; throw new Error('HTTP 404'); }
    if (path.endsWith('/git/commits')) return { sha: 'repair' };
    if (path.endsWith('/pulls')) return { html_url: 'https://example.test/pr/1' };
    return {};
  };
  return { api, calls };
}
const proposal = () => ({ repository: 'owner/repo', source, recipe: 'toolchains',
  files: [{ filename: '.node-version', status: 'modified', additions: 1, deletions: 1, content: '24.20.1\n' }] });
test('publication makes one bounded commit and PR then explicitly dispatches CI', () => {
  const mock = fakeApi();
  assert.equal(publishMaintenance({ ...proposal(), api: mock.api }).created, true);
  assert.deepEqual(mock.calls.filter(call => call.method === 'POST').map(call => call.path.split('/').slice(-2).join('/')),
    ['git/trees', 'git/commits', 'git/refs', 'repo/pulls', 'ci.yml/dispatches']);
  const commit = mock.calls.find(call => call.path.endsWith('/git/commits'));
  assert.deepEqual(commit.body.parents, [source]);
  assert.ok(commit.body.message.includes('SumaFile-Maintenance: toolchains'));
});
test('publication refuses a changed main before any write', () => {
  const mock = fakeApi({ moved: true });
  assert.throws(() => publishMaintenance({ ...proposal(), api: mock.api }), /Main moved/);
  assert.equal(mock.calls.some(call => call.method), false);
});
test('publication recovers a branch created before interruption', () => {
  const mock = fakeApi({ existing: true });
  assert.equal(publishMaintenance({ ...proposal(), api: mock.api }).created, true);
  assert.equal(mock.calls.some(call => call.path.endsWith('/git/refs')), false);
});
test('publication deduplicates closed PRs and recovers a missing CI dispatch on an open PR', () => {
  for (const state of ['closed', 'open']) {
    const mock = fakeApi({ prior: [{ state, head: { sha: 'repair' }, html_url: 'https://example.test/pr/1' }] });
    assert.equal(publishMaintenance({ ...proposal(), api: mock.api }).created, false);
    const writes = mock.calls.filter(call => call.method);
    assert.equal(writes.length, state === 'open' ? 1 : 0);
    if (state === 'open') assert.ok(writes[0].path.endsWith('/dispatches'));
  }
});

test('publication explains disabled workflow PR creation and preserves recovery branch', () => {
  const mock = fakeApi();
  const api = (path, options = {}) => {
    if (path.endsWith('/pulls') && options.method === 'POST') throw new Error('GitHub Actions is not permitted to create or approve pull requests. (HTTP 403)');
    return mock.api(path, options);
  };
  assert.throws(() => publishMaintenance({ ...proposal(), api }), /--permissions-only/);
  assert.ok(mock.calls.some(c => c.path.endsWith('/git/refs')));
  assert.equal(mock.calls.some(c => c.path.endsWith('/dispatches')), false);
});

// ---- The nuget-locks recipe: Dependabot updates a project's lock file but not those of the projects that
// reference it, so a NuGet update fails the locked restore with NU1004 on the branch and never on main.
const dependabotRun = (log = 'error NU1004: The project references simplefile.core whose dependencies has changed.') => {
  const input = fixture('Run WinUI tests', log);
  input.jobs[0].name = 'WinUI host';
  input.run.head_branch = 'dependabot/nuget/src-winui/SimpleFile.Core/minor-and-patch-7a1e3c8403';
  input.run.head_sha = 'c'.repeat(40);
  input.run.pull_requests = [{ number: 80 }];
  input.branchHead = 'c'.repeat(40);
  return input;
};
test('a Dependabot NuGet branch failing the locked restore gets the nuget-locks recipe', () => {
  const plan = classifyFailure(dependabotRun());
  assert.equal(plan.action, 'prepare');
  assert.equal(plan.recipe, 'nuget-locks');
  assert.equal(plan.pr, 80);
});
test('the nuget-locks recipe needs the lock-file error on a current Dependabot NuGet branch with one PR', () => {
  for (const change of [
    input => { input.log = 'error CS1001: Identifier expected'; },
    input => { input.run.head_branch = 'dependabot/cargo/rusqlite-0.43.0'; },
    input => { input.run.head_branch = 'feature/locks'; },
    input => { input.run.pull_requests = []; },
    input => { input.branchHead = 'd'.repeat(40); },
    input => { input.run.head_repository.full_name = 'fork/repo'; },
  ]) {
    const input = dependabotRun(); change(input);
    assert.notEqual(classifyFailure(input).recipe, 'nuget-locks');
    assert.equal(classifyFailure(input).action, 'review');
  }
});
test('the nuget-locks allowlist is the project files and lock files of the .NET projects', () => {
  const file = path => ({ filename: path, status: 'modified', additions: 1, deletions: 1 });
  for (const path of ['src-winui/SimpleFile.Core/SimpleFile.Core.csproj', 'src-winui/SimpleFile.App/packages.lock.json',
    'src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/packages.lock.json', 'plugins/git/ui/SumaFile.Git/packages.lock.json',
    'plugins/remote/ui/SumaFile.Remote.Model/SumaFile.Remote.Model.csproj', 'templates/sumafile-plugin/ui/Example.Plugin/packages.lock.json']) {
    assert.equal(validateRepairFiles('nuget-locks', [file(path)]), true, path);
  }
  for (const path of ['src-winui/Directory.Build.props', 'src-winui/SimpleFile.App/MainWindow.xaml.cs', 'Cargo.lock',
    'src-winui/SimpleFile.sln', '.github/workflows/ci.yml', 'package-lock.json']) {
    assert.equal(validateRepairFiles('nuget-locks', [file(path)]), false, path);
  }
});
test('nuget-locks provenance names the Dependabot pull request and the head it was read at', () => {
  const sha = 'c'.repeat(40);
  const message = `Maintain nuget-locks\n\nSumaFile-Maintenance: nuget-locks\nSumaFile-Source: ${source}\nSumaFile-Dependabot: 80@${sha}`;
  assert.deepEqual(maintenanceMetadata(message), { recipe: 'nuget-locks', source, dependabot: { number: 80, sha } });
  // Without its provenance a nuget-locks proposal is not a recipe result.
  assert.equal(maintenanceMetadata(`SumaFile-Maintenance: nuget-locks\nSumaFile-Source: ${source}`), null);
  // Other recipes carry none.
  assert.deepEqual(maintenanceMetadata(`SumaFile-Maintenance: toolchains\nSumaFile-Source: ${source}`), { recipe: 'toolchains', source });
});
test('a nuget-locks publication records its provenance and names the pull request it supersedes', () => {
  const mock = fakeApi();
  const sha = 'c'.repeat(40);
  const files = [{ filename: 'src-winui/SimpleFile.Core/SimpleFile.Core.csproj', status: 'modified', additions: 3, deletions: 3, content: '<Project />\n' },
    { filename: 'src-winui/SimpleFile.App/packages.lock.json', status: 'modified', additions: 5, deletions: 5, content: '{}\n' }];
  assert.equal(publishMaintenance({ repository: 'owner/repo', source, recipe: 'nuget-locks', files, dependabot: { number: 80, sha }, api: mock.api }).created, true);
  const commit = mock.calls.find(call => call.path.endsWith('/git/commits'));
  assert.ok(commit.body.message.includes(`SumaFile-Dependabot: 80@${sha}`), commit.body.message);
  const pr = mock.calls.find(call => call.path.endsWith('/repo/pulls'));
  assert.match(pr.body.body, /#80/);
  assert.throws(() => publishMaintenance({ repository: 'owner/repo', source, recipe: 'nuget-locks', files, api: fakeApi().api }), /provenance/);
});
