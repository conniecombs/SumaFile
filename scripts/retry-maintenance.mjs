import { githubApi } from './lib/github.mjs';
const repository = process.env.GITHUB_REPOSITORY;
const id = process.env.RUN_ID;
if (!/^\d+$/.test(id)) throw new Error('Invalid run ID');
const run = githubApi(`repos/${repository}/actions/runs/${id}`);
if (run.run_attempt !== 1 || run.status !== 'completed' || run.conclusion !== 'failure') {
  console.log('Run changed since assessment; no retry.');
} else if (run.head_repository?.full_name !== repository) {
  throw new Error('Refusing to retry an external repository run');
} else {
  const current = githubApi(`repos/${repository}/branches/${encodeURIComponent(run.head_branch)}`);
  if (current.commit.sha !== run.head_sha) console.log('Branch moved; no retry.');
  else githubApi(`repos/${repository}/actions/runs/${id}/rerun-failed-jobs`, { method: 'POST' });
}
