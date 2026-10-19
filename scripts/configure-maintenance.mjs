import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { githubApi } from './lib/github.mjs';

const args = process.argv.slice(2);
if (args.includes('--plan') && args.includes('--apply')) throw new Error('Choose either --plan or --apply; no settings were changed.');
const repo = args.find(arg => arg.startsWith('--repo='))?.slice(7) ?? 'conniecombs/SumaFile';
if (!/^[\w.-]+\/[\w.-]+$/.test(repo)) throw new Error('Invalid repository name');
const protection = JSON.parse(readFileSync(resolve(import.meta.dirname, '../.github/branch-protection.json'), 'utf8'));
const plan = [
  { method: 'PUT', endpoint: `repos/${repo}/branches/main/protection`, body: protection },
  { method: 'PATCH', endpoint: `repos/${repo}`, body: { allow_auto_merge: true } },
  { method: 'PUT', endpoint: `repos/${repo}/actions/permissions/workflow`,
    body: { default_workflow_permissions: 'read', can_approve_pull_request_reviews: true } },
];
// Offline preview is the default. Applying requires a deliberate, separate invocation.
if (!args.includes('--apply')) {
  console.log(JSON.stringify({ applied: false, plan }, null, 2));
} else {
  let existing;
  try { existing = githubApi(plan[0].endpoint); }
  catch (error) { if (!error.message.includes('Branch not protected')) throw error; }
  // Never silently replace a pre-existing policy with this repository's baseline.
  if (existing) throw new Error('Branch is already protected. Review and merge its policy manually; no settings were changed.');
  const head = githubApi(`repos/${repo}/branches/main`).commit.sha;
  const checks = githubApi(`repos/${repo}/commits/${head}/check-runs?per_page=100`).check_runs;
  if (!checks.some(check => check.name === 'CI required' && check.conclusion === 'success' && check.app?.slug === 'github-actions')) {
    throw new Error('Install this change and pass CI required on main before enabling protection. No settings were changed.');
  }
  for (const request of plan) githubApi(request.endpoint, request);
  const applied = githubApi(plan[0].endpoint);
  if (!applied.required_status_checks?.strict || !applied.enforce_admins?.enabled
      || !applied.required_status_checks.contexts.includes('CI required')) throw new Error('Protection verification failed');
  console.log('Branch protection and maintenance permissions applied and verified.');
}
