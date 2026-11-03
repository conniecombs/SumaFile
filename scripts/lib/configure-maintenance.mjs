export function configureMaintenance({ api, repository, protection, permissionsOnly = false }) {
  const repo = `repos/${repository}`;
  if (!/^[\w.-]+\/[\w.-]+$/.test(repository)) throw new Error('Invalid repository');
  if (!permissionsOnly) {
    let existing;
    try { existing = api(`${repo}/branches/main/protection`); }
    catch (error) { if (!/Branch not protected/i.test(error.message)) throw error; }
    if (existing) {
      if (!existing.required_status_checks?.strict || !existing.enforce_admins?.enabled
          || !existing.required_status_checks.contexts?.includes('CI required')) {
        throw new Error('Existing protection needs manual review; use --permissions-only to repair PR creation independently.');
      }
    } else {
      const head = api(`${repo}/branches/main`).commit.sha;
      const checks = api(`${repo}/commits/${head}/check-runs?per_page=100`).check_runs;
      if (!checks.some(c => c.name === 'CI required' && c.conclusion === 'success' && c.app?.slug === 'github-actions')) {
        throw new Error('Pass CI required on main before installing branch protection; --permissions-only can repair PR creation now.');
      }
      api(`${repo}/branches/main/protection`, { method: 'PUT', body: protection });
      const applied = api(`${repo}/branches/main/protection`);
      if (!applied.required_status_checks?.strict || !applied.enforce_admins?.enabled
          || !applied.required_status_checks.contexts?.includes('CI required')) throw new Error('Branch protection verification failed');
    }
  }
  const endpoint = `${repo}/actions/permissions/workflow`;
  const current = api(endpoint);
  if (!['read', 'write'].includes(current.default_workflow_permissions)) throw new Error('Unknown workflow permission response');
  if (current.can_approve_pull_request_reviews !== true) {
    api(endpoint, { method: 'PUT', body: {
      default_workflow_permissions: current.default_workflow_permissions,
      can_approve_pull_request_reviews: true,
    } });
  }
  const verified = api(endpoint);
  if (verified.can_approve_pull_request_reviews !== true || verified.default_workflow_permissions !== current.default_workflow_permissions) {
    throw new Error('PR creation permission verification failed; check organization policy');
  }
  // The merge workflow uses a guarded direct merge, not GitHub auto-merge.
  // Do not broaden repository auto-merge settings or replace existing rules.
  return { permissionsVerified: true, protectionPreserved: true, permissionsOnly };
}
