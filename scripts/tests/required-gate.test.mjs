import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { parseWorkflow } from '../lib/workflow-policy.mjs';

const workflow = parseWorkflow(readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8'));
const command = /^node -e '([^']+)'\s*$/.exec(workflow.jobs.required.steps[0].run)[1];
const invoke = (results, required = true) => spawnSync(process.execPath, ['-e', command], {
  env: { ...process.env, RESULTS: JSON.stringify(results), REQUIRE_INSTALLER: String(required) }, encoding: 'utf8',
}).status;
const successful = () => Object.fromEntries(workflow.jobs.required.needs.map(name => [name, { result: 'success' }]));
test('actual aggregate command fails closed for each failed, skipped or cancelled required gate', () => {
  assert.equal(invoke(successful()), 0);
  for (const name of workflow.jobs.required.needs) for (const result of ['failure', 'cancelled', 'skipped']) {
    const jobs = successful(); jobs[name].result = result; assert.notEqual(invoke(jobs), 0, `${name}: ${result}`);
  }
});
test('only optional installer skips are accepted on ordinary human PRs', () => {
  const jobs = successful(); jobs.installer.result = 'skipped';
  assert.equal(invoke(jobs, false), 0);
  jobs.quality.result = 'skipped'; assert.notEqual(invoke(jobs, false), 0);
});
test('prepared branch protection requires the aggregate check without administrator bypass', () => {
  const policy = JSON.parse(readFileSync(new URL('../../.github/branch-protection.json', import.meta.url), 'utf8'));
  assert.equal(policy.enforce_admins, true);
  assert.equal(policy.required_status_checks.strict, true);
  assert.deepEqual(policy.required_status_checks.contexts, ['CI required']);
  assert.equal(policy.allow_force_pushes, false);
});
