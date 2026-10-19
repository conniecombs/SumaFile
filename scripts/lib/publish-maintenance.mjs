import { createHash } from 'node:crypto';
import { limits, validateRepairFiles } from './maintenance-policy.mjs';

// The API is injected so tests exercise the complete publication/recovery path
// without touching GitHub. No push, approval or merge happens in this function.
export function publishMaintenance({ api, repository, source, recipe, files }) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(repository) || !/^[a-f0-9]{40}$/.test(source)) throw new Error('Invalid source');
  if (!validateRepairFiles(recipe, files) || files.some(file => typeof file.content !== 'string'
      || Buffer.byteLength(file.content) > limits.fileBytes || file.content.includes('\0'))) throw new Error('Repair exceeds the allowlist or size limits');
  const repo = `repos/${repository}`;
  if (api(`${repo}/branches/main`).commit.sha !== source) throw new Error('Main moved; recompute the repair on its new head');
  const fingerprint = createHash('sha256').update(JSON.stringify(files.map(file => [file.filename, file.content]).sort())).digest('hex').slice(0, 12);
  const branch = `connie/maintenance/${recipe}-${source.slice(0, 12)}-${fingerprint}`;
  const prior = api(`${repo}/pulls?state=all&head=${repository.split('/')[0]}:${branch}&per_page=100`);
  if (prior.length) {
    if (prior[0].state === 'open') {
      const runs = api(`${repo}/actions/workflows/ci.yml/runs?head_sha=${prior[0].head.sha}&per_page=1`).workflow_runs;
      if (!runs.length) api(`${repo}/actions/workflows/ci.yml/dispatches`, { method: 'POST', body: { ref: branch } });
    }
    return { created: false, reason: 'This exact maintenance proposal already exists', url: prior[0].html_url };
  }
  const base = api(`${repo}/git/commits/${source}`);
  const tree = api(`${repo}/git/trees`, { method: 'POST', body: { base_tree: base.tree.sha,
    tree: files.map(file => ({ path: file.filename, mode: '100644', type: 'blob', content: file.content })) } });
  let existing;
  try { existing = api(`${repo}/git/ref/heads/${branch}`); }
  catch (error) { if (!/\b404\b/.test(error.message)) throw error; }
  if (existing) {
    const commit = api(`${repo}/git/commits/${existing.object.sha}`);
    if (commit.tree.sha !== tree.sha || commit.parents.length !== 1 || commit.parents[0].sha !== source) throw new Error('Existing branch does not match this proposal');
  } else {
    const commit = api(`${repo}/git/commits`, { method: 'POST', body: {
      message: `Maintain ${recipe}\n\nSumaFile-Maintenance: ${recipe}\nSumaFile-Source: ${source}`,
      tree: tree.sha, parents: [source],
    } });
    api(`${repo}/git/refs`, { method: 'POST', body: { ref: `refs/heads/${branch}`, sha: commit.sha } });
  }
  const pr = api(`${repo}/pulls`, { method: 'POST', body: {
    title: `Maintenance: ${recipe}`, head: branch, base: 'main',
    body: `Applies the bounded ${recipe} recipe to main at ${source}.\n\nCI must validate this exact head, including Windows UI and installer checks. Automatic merging also requires enforced, strict branch protection. Release version changes require human review.`,
  } });
  // GITHUB_TOKEN-created PR events do not start CI. Dispatch is the documented
  // exception; the successful run then invokes the separate trusted merge gate.
  api(`${repo}/actions/workflows/ci.yml/dispatches`, { method: 'POST', body: { ref: branch } });
  return { created: true, url: pr.html_url };
}
