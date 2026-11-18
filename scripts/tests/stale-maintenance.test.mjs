import assert from 'node:assert/strict';
import test from 'node:test';
import { closeStaleMaintenanceProposals, staleMaintenanceReason } from '../lib/stale-maintenance.mjs';

const main = 'a'.repeat(40);
const older = 'b'.repeat(40);
const head = 'c'.repeat(40);
const proposal = ({ number = 91, source = main, branch = 'connie/maintenance/nuget-locks-aaaaaaaaaaaa-000000000000', dependabot = `80@${head}` } = {}) => ({
  number, state: 'open', user: { login: 'github-actions[bot]' }, base: { ref: 'main' },
  head: { ref: branch, sha: 'd'.repeat(40) },
  message: `Maintain nuget-locks\n\nSumaFile-Maintenance: nuget-locks\nSumaFile-Source: ${source}${dependabot ? `\nSumaFile-Dependabot: ${dependabot}` : ''}`,
});

// A proposal is a single commit on the main it was computed from. Once main moves it can never merge (the gate
// insists on current main), and a lock-file repair is pointless once its Dependabot pull request is gone.
test('a proposal computed on an older main is stale', () => {
  assert.match(staleMaintenanceReason({ metadata: { recipe: 'toolchains', source: older }, mainSha: main }), /main moved/);
  assert.equal(staleMaintenanceReason({ metadata: { recipe: 'toolchains', source: main }, mainSha: main }), null);
});
test('a lock-file repair whose Dependabot pull request closed is stale', () => {
  const metadata = { recipe: 'nuget-locks', source: main, dependabot: { number: 80, sha: head } };
  assert.match(staleMaintenanceReason({ metadata, mainSha: main, dependabotPull: { state: 'closed', merged_at: null } }), /#80 .*closed/);
  assert.match(staleMaintenanceReason({ metadata, mainSha: main, dependabotPull: { state: 'closed', merged_at: '2026-11-18T22:44:00Z' } }), /#80 .*merged/);
  assert.equal(staleMaintenanceReason({ metadata, mainSha: main, dependabotPull: { state: 'open', head: { sha: head } } }), null);
  // Dependabot pushed a new head: the versions may differ, so the repair is recomputed from the failing run.
  assert.match(staleMaintenanceReason({ metadata, mainSha: main, dependabotPull: { state: 'open', head: { sha: 'e'.repeat(40) } } }), /#80 moved/);
});

function fakeApi(pulls, dependabotState = 'open') {
  const calls = [];
  const api = (path, options = {}) => {
    calls.push({ path, ...options });
    if (path === 'repos/owner/repo/branches/main') return { commit: { sha: main } };
    if (path.startsWith('repos/owner/repo/pulls?')) return pulls;
    const commit = /^repos\/owner\/repo\/commits\/(\w+)$/.exec(path);
    if (commit) return { commit: { message: pulls.find(pull => pull.head.sha === commit[1]).message } };
    if (path === 'repos/owner/repo/pulls/80') return { number: 80, state: dependabotState, merged_at: null, head: { sha: head } };
    return {};
  };
  return { api, calls };
}
test('the sweep closes stale proposals with a note and leaves current ones open', () => {
  const stale = proposal({ number: 91, source: older });
  const current = proposal({ number: 92, source: main, branch: 'connie/maintenance/nuget-locks-aaaaaaaaaaaa-111111111111' });
  current.head.sha = 'f'.repeat(40);
  const { api, calls } = fakeApi([stale, current]);
  const closed = closeStaleMaintenanceProposals({ api, repository: 'owner/repo' });
  assert.deepEqual(closed, [{ number: 91, reason: closed[0].reason }]);
  assert.match(closed[0].reason, /main moved/);
  const writes = calls.filter(call => call.method);
  assert.deepEqual(writes.map(call => [call.method, call.path]), [
    ['POST', 'repos/owner/repo/issues/91/comments'],
    ['PATCH', 'repos/owner/repo/pulls/91'],
  ]);
  assert.equal(writes[1].body.state, 'closed');
  assert.match(writes[0].body.body, /main moved/);
});
test('the sweep touches only open maintenance proposals by the workflow bot', () => {
  const human = { ...proposal({ number: 5, source: older, branch: 'feature/x' }), user: { login: 'someone' } };
  human.head.sha = '1'.repeat(40);
  const dependabot = { ...proposal({ number: 6, source: older, branch: 'dependabot/nuget/x' }), user: { login: 'dependabot[bot]' } };
  dependabot.head.sha = '2'.repeat(40);
  const { api, calls } = fakeApi([human, dependabot]);
  assert.deepEqual(closeStaleMaintenanceProposals({ api, repository: 'owner/repo' }), []);
  assert.equal(calls.filter(call => call.method).length, 0);
});
