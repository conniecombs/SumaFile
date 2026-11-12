import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';
import { parseWorkflow, validateWorkflows } from '../lib/workflow-policy.mjs';

const root = resolve(import.meta.dirname, '../..');
const source = () => ({
  workflows: Object.fromEntries(readdirSync(resolve(root, '.github/workflows')).filter(name => /\.ya?ml$/.test(name))
    .map(name => [name, parseWorkflow(readFileSync(resolve(root, '.github/workflows', name), 'utf8'))])),
  dependabot: parseWorkflow(readFileSync(resolve(root, '.github/dependabot.yml'), 'utf8')),
});
test('all configured workflow contracts pass', () => {
  const { workflows, dependabot } = source();
  assert.deepEqual(validateWorkflows(workflows, dependabot), []);
});
test('a coordinated action update does not require changing the checker', () => {
  const { workflows, dependabot } = source();
  for (const workflow of Object.values(workflows)) for (const job of Object.values(workflow.jobs)) {
    for (const step of job.steps ?? []) if (step.uses?.startsWith('actions/upload-artifact@')) step.uses = 'actions/upload-artifact@v999';
  }
  assert.deepEqual(validateWorkflows(workflows, dependabot), []);
});
test('comments cannot supply missing tests, permissions or dependencies', () => {
  const { workflows, dependabot } = source();
  workflows['ci.yml'].jobs.quality.steps = [{ run: '# cargo test --locked --all-features' }];
  workflows['ci.yml'].jobs.build.needs = [];
  workflows['ci.yml'].permissions = 'write-all';
  const errors = validateWorkflows(workflows, dependabot);
  assert.ok(errors.some(error => error.includes('locked Rust tests')));
  assert.ok(errors.some(error => error.includes('must depend on security')));
  assert.ok(errors.some(error => error.includes('scoped permissions')));
});
test('unsafe failure suppression and partial action upgrades fail', () => {
  const { workflows, dependabot } = source();
  workflows['ci.yml'].jobs.quality.steps[0]['continue-on-error'] = true;
  workflows['ci.yml'].jobs.quality.steps[0].uses = 'actions/checkout@v999';
  const errors = validateWorkflows(workflows, dependabot);
  assert.ok(errors.some(error => error.includes('ignore step failure')));
  assert.ok(errors.some(error => error.includes('inconsistent refs')));
});
test('YAML semantics accept quoting and reject duplicate keys', () => {
  assert.deepEqual(parseWorkflow('"on": {push: {branches: ["main", "C#"]}}'),
    parseWorkflow("on:\n  push:\n    branches:\n      - main\n      - 'C#'\n"));
  assert.throws(() => parseWorkflow('jobs: {}\njobs: {}'), /unique/i);
});

test('release uploads cannot replace reviewed notes with a generic message', () => {
  const { workflows, dependabot } = source();
  const upload = workflows['release.yml'].jobs.build.steps.find(step => step.uses?.startsWith('softprops/action-gh-release@'));
  delete upload.with.body_path;
  upload.with.body = 'Download the attached files.';
  assert.ok(validateWorkflows(workflows, dependabot).some(error => error.includes('version-specific release notes')));
});
test('the installer workflows resolve WiX with the build script\'s own Resolve-WixBin', () => {
  // scripts/lib/wix-msi.ps1 refuses WiX older than 3.14.1 or without WixUtilExtension, so a workflow copy without that
  // gate could find an old WiX, skip the bootstrap and fail the tag or dispatch build that PR CI never runs.
  const { workflows, dependabot } = source();
  for (const [file, id] of [['release.yml', 'build'], ['release-build.yml', 'windows-release-build'], ['installer-smoke.yml', 'windows-installer-smoke']]) {
    const step = workflows[file].jobs[id].steps.find(current => current.name === 'Install WiX Toolset (MSI)');
    assert.ok(step, `${file}: has the WiX step`);
    assert.match(step.run, /^\. \.\/scripts\/lib\/wix-msi\.ps1$/m, `${file}: dot-sources the shared resolver`);
    assert.doesNotMatch(step.run, /function Resolve-WixBin/, `${file}: no copy of Resolve-WixBin`);
    // The resolver is checked before the bootstrap, so a WiX that is present but too old still gets installed.
    assert.ok(step.run.indexOf('Resolve-WixBin') < step.run.indexOf('choco install wixtoolset'));
  }
  const broken = source();
  const step = broken.workflows['release.yml'].jobs.build.steps.find(current => current.name === 'Install WiX Toolset (MSI)');
  step.run = step.run.replace('. ./scripts/lib/wix-msi.ps1', '');
  assert.ok(validateWorkflows(broken.workflows, broken.dependabot).some(error => error.includes('shared WiX resolver')));
  const lifecycle = source();
  lifecycle.workflows['release.yml'].jobs.build.steps = lifecycle.workflows['release.yml'].jobs.build.steps
    .filter(current => !/smoke:winui-msi-lifecycle/.test(current.run ?? ''));
  assert.ok(validateWorkflows(lifecycle.workflows, lifecycle.dependabot).some(error => error.includes('MSI lifecycle smoke')));
  assert.deepEqual(validateWorkflows(workflows, dependabot), []);
});
test('the dispatched merge-gate handoff stays narrow and gated', () => {
  const { workflows, dependabot } = source();
  workflows['ci.yml'].jobs['merge-gate'].permissions = { actions: 'write', contents: 'write' };
  workflows['ci.yml'].jobs['merge-gate'].if = "github.event_name == 'workflow_dispatch'";
  delete workflows['dependabot-automerge.yml'].on.workflow_dispatch;
  const errors = validateWorkflows(workflows, dependabot);
  assert.ok(errors.some(error => error.includes('may only dispatch workflows')));
  assert.ok(errors.some(error => error.includes('successful required gate')));
  assert.ok(errors.some(error => error.includes('dispatched CI run id')));
});
