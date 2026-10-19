import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { githubApi } from './lib/github.mjs';
import { publishMaintenance } from './lib/publish-maintenance.mjs';

const git = args => execFileSync('git', args, { encoding: 'utf8' }).trim();
const source = process.env.MAINTENANCE_SOURCE;
if (git(['rev-parse', 'HEAD']) !== source) throw new Error('Checkout must match the assessed main head');
if (git(['diff', '--name-only', '--diff-filter=ACDRTUXB']).length) throw new Error('Only modifications to existing files are allowed');
if (git(['ls-files', '--others', '--exclude-standard']).length) throw new Error('Unexpected untracked files');
const diff = git(['diff', '--numstat']);
if (!diff) { console.log('No update needed.'); process.exit(0); }
const files = diff.split('\n').map(line => {
  const [additions, deletions, filename] = line.split('\t');
  return { filename, status: 'modified', additions: Number(additions), deletions: Number(deletions), content: readFileSync(filename, 'utf8') };
});
console.log(JSON.stringify(publishMaintenance({ api: githubApi, repository: process.env.GITHUB_REPOSITORY,
  source, recipe: process.env.MAINTENANCE_RECIPE, files }), null, 2));
