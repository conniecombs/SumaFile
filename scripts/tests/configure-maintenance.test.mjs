import assert from 'node:assert/strict';
import test from 'node:test';
import { configureMaintenance } from '../lib/configure-maintenance.mjs';
function fixture({ protection = { required_status_checks: { strict: true, contexts: ['CI required', 'Extra check'] }, enforce_admins: { enabled: true } }, enabled = false, blocked = false } = {}) {
  const calls = [];
  let permission = { default_workflow_permissions: 'read', can_approve_pull_request_reviews: enabled };
  const api = (path, options = {}) => {
    calls.push({ path, ...options });
    if (path.endsWith('/protection')) return protection;
    if (path.endsWith('/workflow')) {
      if (options.method === 'PUT' && !blocked) permission = options.body;
      return permission;
    }
    throw new Error('Unexpected API request');
  };
  return { calls, api };
}
test('repair permissions with existing protection and preserve all rules', () => {
  const mock = fixture();
  assert.equal(configureMaintenance({ api: mock.api, repository: 'owner/repo' }).permissionsVerified, true);
  assert.deepEqual(mock.calls.filter(c => c.method).map(c => c.path), ['repos/owner/repo/actions/permissions/workflow']);
});
test('permissions-only repair is independent of protection and failing main', () => {
  const mock = fixture({ protection: {} });
  configureMaintenance({ api: mock.api, repository: 'owner/repo', permissionsOnly: true });
  assert.ok(mock.calls.every(c => c.path.endsWith('/workflow')));
});
test('already active permission is idempotent; blocked verification fails', () => {
  const mock = fixture({ enabled: true });
  configureMaintenance({ api: mock.api, repository: 'owner/repo', permissionsOnly: true });
  assert.equal(mock.calls.some(c => c.method), false);
  const blocked = fixture({ blocked: true });
  assert.throws(() => configureMaintenance({ api: blocked.api, repository: 'owner/repo', permissionsOnly: true }), /verification failed/);
});
test('inadequate existing protection is never overwritten', () => {
  const mock = fixture({ protection: {} });
  assert.throws(() => configureMaintenance({ api: mock.api, repository: 'owner/repo' }), /manual review/);
  assert.equal(mock.calls.some(c => c.method), false);
});
