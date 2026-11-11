import { readFileSync } from 'node:fs';
import { gh, githubApi, githubPages } from './lib/github.mjs';
import { evaluateMerge, onlyApprovalPendingRunsBlock } from './lib/merge-policy.mjs';
import { verifyMaintenance } from './verify-maintenance.mjs';
import { maintenanceMetadata } from './lib/maintenance-policy.mjs';
import { currentRunJobs } from './lib/workflow-jobs.mjs';

const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
const repository = process.env.GITHUB_REPOSITORY;
// A CI run that the workflow token dispatched (maintenance proposals) completes without a workflow_run event, so its
// last job dispatches this gate with the run id instead. Either way the run is re-read from the API and assessed by
// the same policy; the event only names it.
const runId = assessedRunId(event);
const run = await completedRun(runId);
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
  const onlyApprovalPending = pr.mergeable_state === 'unstable' && onlyApprovalPendingRunsBlock({
    assessedRunId: run.id,
    headSha: pr.head.sha,
    runs: githubPages(`repos/${repository}/actions/runs?head_sha=${pr.head.sha}&per_page=100`).flatMap(page => page.workflow_runs),
    suites: githubPages(`repos/${repository}/commits/${pr.head.sha}/check-suites?per_page=100`).flatMap(page => page.check_suites),
    statuses: githubApi(`repos/${repository}/commits/${pr.head.sha}/status`).statuses ?? [],
  });
  const input = { repository, pr, run, jobs, protection, commit, files, onlyApprovalPending };
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

function assessedRunId(payload) {
  const id = payload.workflow_run?.id ?? Number(payload.inputs?.run_id);
  if (!Number.isSafeInteger(id) || id <= 0) throw new Error('No CI run to assess');
  return id;
}

/** The run once it has completed: a dispatched gate starts while the run's last job is still finishing. */
async function completedRun(id, { attempts = 80, delayMs = 15_000 } = {}) {
  for (let attempt = 1; ; attempt++) {
    const current = githubApi(`repos/${repository}/actions/runs/${id}`);
    if (current.status === 'completed' || attempt >= attempts) return current;
    await new Promise(resolve => setTimeout(resolve, delayMs));
  }
}
