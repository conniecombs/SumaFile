import { spawnSync } from 'node:child_process';

export function gh(args, options = {}) {
  const result = spawnSync('gh', args, { encoding: 'utf8', maxBuffer: 20 * 1024 * 1024, ...options });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`gh ${args[0]} failed: ${result.stderr.trim()}`);
  return result.stdout;
}

export function githubApi(endpoint, { method = 'GET', body } = {}) {
  const args = ['api', endpoint, '--method', method];
  if (body !== undefined) args.push('--input', '-');
  const text = gh(args, body === undefined ? {} : { input: JSON.stringify(body) });
  return text.trim() ? JSON.parse(text) : null;
}

export function githubPages(endpoint) {
  return JSON.parse(gh(['api', endpoint, '--paginate', '--slurp']));
}
