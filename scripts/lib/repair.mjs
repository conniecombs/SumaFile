import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { applyProposal, proposalFile, restoreArgs } from './nuget-locks.mjs';

export function runRecipe(recipe, version, cwd = process.cwd()) {
  const commands = {
    security: [[process.execPath, ['scripts/update-security.mjs']]],
    rustfmt: [['cargo', ['fmt', '--all']]],
    generated: [[process.execPath, ['scripts/generate-ipc-bindings.mjs']], [process.execPath, ['scripts/generate-open-with-policy.mjs']]],
    toolchains: [[process.execPath, ['scripts/update-toolchains.mjs', '--write']]],
    release: [[process.execPath, ['scripts/release-version.mjs', version ?? '', '--write']]],
    'nuget-locks': [['dotnet', restoreArgs()]],
  };
  if (!commands[recipe]) throw new Error('Unknown recipe');
  if (recipe === 'nuget-locks') {
    // The proposal was read from GitHub by the caller that holds a token; the recipe itself runs without one.
    applyProposal(cwd, JSON.parse(readFileSync(resolve(cwd, proposalFile), 'utf8')));
  }
  const env = { ...process.env };
  delete env.GH_TOKEN;
  delete env.GITHUB_TOKEN;
  for (const [exe, args] of commands[recipe]) {
    const result = spawnSync(exe, args, { stdio: 'inherit', env, cwd });
    if (result.error || result.status !== 0) throw result.error ?? new Error(`${recipe} failed (${result.status})`);
  }
}
