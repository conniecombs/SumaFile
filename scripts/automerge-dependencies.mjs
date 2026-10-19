import { readFileSync } from 'node:fs';
import { gh, githubApi, githubPages } from './lib/github.mjs';
import { evaluateMerge } from './lib/merge-policy.mjs';
import { verifyMaintenance } from './verify-maintenance.mjs';
import { maintenanceMetadata } from './lib/maintenance-policy.mjs';
import { currentRunJobs } from './lib/workflow-jobs.mjs';

const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
const repository = process.env.GITHUB_REPOSITORY;
const run = githubApi(`repos/${repository}/actions/runs/${event.workflow_run.id}`);
const prs = githubPages(`repos/${repository}/commits/${run.head_sha}/pulls?per_page=100`).flat();
for (const candidate of prs.filter(pr => pr.state === 'open' && pr.head.sha === run.head_sha)) {
  const pr = githubApi(`repos/${repository}/pulls/${candidate.number}`);
  const jobs = currentRunJobs(repository, run);
  let protection;
  try {
    // The REST protection endpoint requires administration permission, which a
    // workflow token must not have. Repository GraphQL exposes the merge rule.
    const [owner, name] = repository.split('/');
    const response = JSON.parse(gh(['api', 'graphql', '-f', 'query=query($owner:String!,$name:String!){repository(owner:$owner,name:$name){ref(qualifiedName:"refs/heads/main"){branchProtectionRule{requiresStatusChecks requiresStrictStatusChecks isAdminEnforced requiredStatusCheckContexts}}}}',
      '-f', `owner=${owner}`, '-f', `name=${name}`]));
    const rule = response.data.repository.ref.branchProtectionRule;
    protection = { enforce_admins: { enabled: rule?.isAdminEnforced },
      required_status_checks: { strict: rule?.requiresStatusChecks && rule?.requiresStrictStatusChecks, contexts: rule?.requiredStatusCheckContexts } };
  }
  catch { console.log('Branch protection could not be verified; refusing automatic merge.'); continue; }
  const commit = githubApi(`repos/${repository}/commits/${pr.head.sha}`);
  const files = githubPages(`repos/${repository}/pulls/${pr.number}/files?per_page=100`).flat();
  const input = { repository, pr, run, jobs, protection, commit, files };
  let decision = evaluateMerge(input);
  if (!decision.eligible && maintenanceMetadata(commit.commit.message)
      && evaluateMerge({ ...input, repairVerified: true }).eligible) {
    decision = evaluateMerge({ ...input, repairVerified: verifyMaintenance({ commit, files, api: githubApi, repository }) });
  }
  console.log(`PR #${pr.number}: ${decision.reason}`);
  if (!decision.eligible) continue;
  // GitHub atomically checks the expected head SHA and branch protection again.
  const result = githubApi(`repos/${repository}/pulls/${pr.number}/merge`, {
    method: 'PUT', body: { sha: pr.head.sha, merge_method: 'squash' },
  });
  if (!result.merged) throw new Error(`GitHub refused PR #${pr.number}: ${result.message}`);
  console.log(`Merged PR #${pr.number} at ${result.sha}`);
}
