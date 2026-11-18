import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { safeDependencyFile } from './merge-policy.mjs';

// Dependabot regenerates the lock file of the project whose package it bumps, but a lock file also records the
// dependencies of every project it references, so the referencing projects fail CI's locked restore with NU1004.
// This recipe applies the pull request's package versions to current main and restores every lock file, as the
// documented `dotnet restore --force-evaluate` does by hand. Restoring on Linux with EnableWindowsTargeting
// reproduces the Windows-generated lock files byte for byte.

export const solution = 'src-winui/SimpleFile.sln';
/** Where the prepare job and the merge gate leave the proposal for the recipe (under the ignored target folder). */
export const proposalFile = 'target/maintenance/dependabot-proposal.json';

export const restoreArgs = () => ['restore', solution, '--force-evaluate', '-p:EnableWindowsTargeting=true'];

const normalize = text => text.replaceAll('\r\n', '\n').replace(/Version="[\d.]+"/g, 'Version="VERSION"');

/** True when two project files differ only in `Version="..."` attributes (and line endings). */
export function sameApartFromPackageVersions(a, b) {
  return normalize(a) === normalize(b);
}

/**
 * The Dependabot pull request's project files, read at `sha`, once the pull request is shown to be Dependabot's
 * own dependency-only update of main. Only versions are taken from it: the restore runs on current main.
 */
export function dependabotProposal({ api, repository, number, sha }) {
  const pull = api(`repos/${repository}/pulls/${number}`);
  if (pull?.user?.login !== 'dependabot[bot]' || pull.base?.ref !== 'main' || pull.head?.repo?.full_name !== repository) {
    throw new Error(`Pull request #${number} is not a Dependabot update of main from this repository`);
  }
  if (pull.head.sha !== sha) throw new Error(`Pull request #${number} head moved from ${sha} to ${pull.head.sha}`);
  const files = api(`repos/${repository}/pulls/${number}/files?per_page=100`);
  if (!Array.isArray(files) || !files.length || files.length > 30 || !files.every(safeDependencyFile)) {
    throw new Error(`Pull request #${number} changes more than package versions and lock files`);
  }
  const csproj = files.filter(file => file.filename.endsWith('.csproj')).map(file => {
    const blob = api(`repos/${repository}/contents/${file.filename}?ref=${sha}`);
    if (blob?.encoding !== 'base64' || typeof blob.content !== 'string') throw new Error(`Could not read ${file.filename} at ${sha}`);
    return { path: file.filename, content: Buffer.from(blob.content, 'base64').toString('utf8') };
  });
  return { number, sha, csproj };
}

/** Writes the proposal's project files over main's, each of which may differ from main only in package versions. */
export function applyProposal(root, proposal) {
  const written = [];
  for (const { path, content } of proposal.csproj) {
    if (typeof path !== 'string' || path.includes('..') || path.startsWith('/') || path.includes('\\') || !path.endsWith('.csproj')) {
      throw new Error(`Refusing to write ${path}`);
    }
    const target = resolve(root, path);
    const current = readFileSync(target, 'utf8');
    if (!sameApartFromPackageVersions(current, content)) throw new Error(`${path} changes more than package versions`);
    writeFileSync(target, content);
    written.push(path);
  }
  return written;
}
