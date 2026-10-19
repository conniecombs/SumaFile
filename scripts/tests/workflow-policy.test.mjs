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
