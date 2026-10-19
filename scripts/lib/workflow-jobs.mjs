import { githubPages } from './github.mjs';

// Partial reruns contain only the jobs that ran again. Preserve a successful
// job from an earlier attempt of the same immutable run/SHA, but always replace
// it when that job appears in a newer attempt, including skipped/failed results.
export function currentRunJobs(repository, run, pages = githubPages) {
  if (!Number.isInteger(run.run_attempt) || run.run_attempt < 1 || run.run_attempt > 10) {
    throw new Error('Unexpected rerun count; manual review required');
  }
  const current = new Map();
  for (let attempt = 1; attempt <= run.run_attempt; attempt++) {
    const jobs = pages(`repos/${repository}/actions/runs/${run.id}/attempts/${attempt}/jobs?per_page=100`).flatMap(page => page.jobs);
    const groups = new Map();
    for (const job of jobs) {
      if (job.run_id !== run.id || job.head_sha !== run.head_sha) throw new Error('Job does not belong to the assessed run and SHA');
      const group = groups.get(job.name) ?? [];
      group.push(job);
      groups.set(job.name, group);
    }
    for (const [name, group] of groups) current.set(name, group);
  }
  return [...current.values()].flat();
}
