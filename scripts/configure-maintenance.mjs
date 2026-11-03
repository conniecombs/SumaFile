import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { githubApi } from './lib/github.mjs';

const args = process.argv.slice(2);
if (args.includes('--plan') && args.includes('--apply')) throw new Error('Choose either --plan or --apply; no settings were changed.');
const repo = args.find(arg => arg.startsWith('--repo='))?.slice(7) ?? 'conniecombs/SumaFile';
if (!/^[\w.-]+\/[\w.-]+$/.test(repo)) throw new Error('Invalid repository name');
const protection = JSON.parse(readFileSync(resolve(import.meta.dirname, '../.github/branch-protection.json'), 'utf8'));
const plan = {
  permissionsOnly: args.includes('--permissions-only'),
  workflowPermissions: { endpoint: `repos/${repo}/actions/permissions/workflow`,
    can_approve_pull_request_reviews: true, default_workflow_permissions: 'preserve existing value' },
  protection: args.includes('--permissions-only') ? 'unchanged' :
    { existing: 'preserve and validate', absent: protection, requiresGreenMain: true },
};
// Offline preview is the default. Applying requires a deliberate, separate invocation.
if (!args.includes('--apply')) {
  console.log(JSON.stringify({ applied: false, plan }, null, 2));
} else {
  const { configureMaintenance } = await import('./lib/configure-maintenance.mjs');
  console.log(JSON.stringify(configureMaintenance({ api: githubApi, repository: repo, protection,
    permissionsOnly: args.includes('--permissions-only') }), null, 2));
}
