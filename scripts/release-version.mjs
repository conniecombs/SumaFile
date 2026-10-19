import { appendFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { planReleaseVersion, readReleaseVersion } from './lib/release-version.mjs';

const root = resolve(import.meta.dirname, '..');
const args = process.argv.slice(2);
if (args.includes('--resolve')) {
  const version = readReleaseVersion(root);
  const requested = process.env.RELEASE_VERSION?.trim()
    || (process.env.GITHUB_REF_TYPE === 'tag' ? process.env.GITHUB_REF_NAME : '') || version.tag;
  if (requested !== version.tag) throw new Error(`Version mismatch: requested=${requested}, repository=${version.tag}`);
  const draft = process.env.RELEASE_DRAFT === 'false' ? 'false' : 'true';
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT,
    `version=${version.tag}\nprerelease=${version.prerelease}\ndraft=${draft}\n`);
  console.log(JSON.stringify({ ...version, draft }));
} else {
  const input = args.find(arg => !arg.startsWith('--'));
  if (!input) throw new Error('Usage: node scripts/release-version.mjs VERSION [--write] (preview by default)');
  const { version, changes } = planReleaseVersion(root, input);
  if (args.includes('--write')) for (const [file, source] of changes) writeFileSync(resolve(root, file), source);
  console.log(JSON.stringify({ applied: args.includes('--write'), version, files: [...changes.keys()] }, null, 2));
}
