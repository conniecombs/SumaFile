import { appendFileSync, readFileSync } from 'node:fs';
import { gh, githubApi, githubPages } from './lib/github.mjs';
import { classifyFailure } from './lib/maintenance-policy.mjs';
import { parseVersion } from './lib/release-version.mjs';

const repository = process.env.GITHUB_REPOSITORY;
const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
const main = githubApi(`repos/${repository}/branches/main`).commit.sha;
let plan;
if (process.env.GITHUB_EVENT_NAME === 'workflow_run') {
  const run = githubApi(`repos/${repository}/actions/runs/${event.workflow_run.id}`);
  const jobsFor = current => githubPages(`repos/${repository}/actions/runs/${current.id}/attempts/${current.run_attempt}/jobs?per_page=100`).flatMap(page => page.jobs);
  const jobs = jobsFor(run);
  const recent = githubApi(`repos/${repository}/actions/workflows/ci.yml/runs?branch=main&event=push&status=completed&per_page=10`).workflow_runs;
  const baseline = recent.find(candidate => candidate.id !== run.id && candidate.head_sha === main);
  let branchHead;
  if (run.head_repository?.full_name === repository) {
    try { branchHead = githubApi(`repos/${repository}/branches/${encodeURIComponent(run.head_branch)}`).commit.sha; }
    catch { branchHead = null; }
  }
  let log = '';
  try { log = gh(['run', 'view', String(run.id), '--repo', repository, '--attempt', String(run.run_attempt), '--log-failed']); }
  catch { /* Missing or oversized logs must result in review, never a blind retry. */ }
  plan = classifyFailure({ run, jobs, log, repository, mainSha: main, branchHead, baseJobs: baseline ? jobsFor(baseline) : [] });
  plan.run_id = String(run.id);
} else {
  const recipe = event.inputs?.recipe ?? 'toolchains';
  if (!['toolchains', 'release', 'rustfmt', 'generated'].includes(recipe)) throw new Error('Unsupported maintenance recipe');
  if (recipe === 'release') parseVersion(event.inputs?.version ?? '');
  plan = { action: 'prepare', recipe, reason: `${recipe} maintenance requested`, version: event.inputs?.version ?? '' };
}
plan.source = main;
console.log(JSON.stringify(plan, null, 2));
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Maintenance assessment\n\n\`\`\`json\n${JSON.stringify(plan, null, 2)}\n\`\`\`\n`);
if (process.env.GITHUB_OUTPUT) {
  for (const key of ['action', 'recipe', 'source', 'run_id', 'version']) {
    const value = String(plan[key] ?? '');
    if (/[\r\n]/.test(value)) throw new Error('Invalid workflow output');
    appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${value}\n`);
  }
}
