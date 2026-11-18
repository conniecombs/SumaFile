import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { githubApi } from './lib/github.mjs';
import { dependabotProposal, proposalFile } from './lib/nuget-locks.mjs';

// Reads the Dependabot pull request a nuget-locks repair takes its package versions from, with the token this
// job holds, and leaves it where the recipe (which runs without a token) finds it.
const repository = process.env.GITHUB_REPOSITORY;
const number = Number(process.env.MAINTENANCE_PR);
if (!repository || !Number.isInteger(number) || number <= 0) throw new Error('MAINTENANCE_PR must name the Dependabot pull request');
const sha = githubApi(`repos/${repository}/pulls/${number}`).head.sha;
const proposal = dependabotProposal({ api: githubApi, repository, number, sha });
const target = resolve(process.cwd(), proposalFile);
mkdirSync(dirname(target), { recursive: true });
writeFileSync(target, JSON.stringify(proposal, null, 2));
console.log(`Dependabot #${number} at ${sha}: ${proposal.csproj.map(file => file.path).join(', ') || 'lock files only'}`);
