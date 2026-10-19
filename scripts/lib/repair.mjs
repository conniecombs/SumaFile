import { spawnSync } from 'node:child_process';

export function runRecipe(recipe, version, cwd = process.cwd()) {
  const commands = {
    rustfmt: [['cargo', ['fmt', '--all']]],
    generated: [[process.execPath, ['scripts/generate-ipc-bindings.mjs']], [process.execPath, ['scripts/generate-open-with-policy.mjs']]],
    toolchains: [[process.execPath, ['scripts/update-toolchains.mjs', '--write']]],
    release: [[process.execPath, ['scripts/release-version.mjs', version ?? '', '--write']]],
  };
  if (!commands[recipe]) throw new Error('Unknown recipe');
  const env = { ...process.env };
  delete env.GH_TOKEN;
  delete env.GITHUB_TOKEN;
  for (const [exe, args] of commands[recipe]) {
    const result = spawnSync(exe, args, { stdio: 'inherit', env, cwd });
    if (result.error || result.status !== 0) throw result.error ?? new Error(`${recipe} failed (${result.status})`);
  }
}
