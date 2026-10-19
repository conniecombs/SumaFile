import assert from 'node:assert/strict';
import test from 'node:test';
import { currentRunJobs } from '../lib/workflow-jobs.mjs';

const run = { id: 123, head_sha: 'abc', run_attempt: 2 };
const job = (name, conclusion) => ({ name, conclusion, run_id: run.id, head_sha: run.head_sha });
const pages = attempts => endpoint => [{ jobs: attempts[Number(/attempts\/(\d+)/.exec(endpoint)[1]) - 1] }];
test('a partial rerun retains successful checks from the same run and SHA', () => {
  const jobs = currentRunJobs('owner/repo', run, pages([
    [job('quality', 'success'), job('setup', 'failure')], [job('setup', 'success')],
  ]));
  assert.deepEqual(jobs.map(item => [item.name, item.conclusion]), [['quality', 'success'], ['setup', 'success']]);
});
test('a later failed or skipped gate overrides its earlier success', () => {
  for (const conclusion of ['failure', 'skipped', 'cancelled']) {
    const jobs = currentRunJobs('owner/repo', run, pages([[job('quality', 'success')], [job('quality', conclusion)]]));
    assert.equal(jobs[0].conclusion, conclusion);
  }
});
test('duplicate current gates are preserved for merge-policy rejection and foreign SHAs are refused', () => {
  assert.equal(currentRunJobs('owner/repo', run, pages([[], [job('quality', 'success'), job('quality', 'success')]])).length, 2);
  assert.throws(() => currentRunJobs('owner/repo', run, pages([[{ ...job('quality', 'success'), head_sha: 'other' }], []])), /SHA/);
});
