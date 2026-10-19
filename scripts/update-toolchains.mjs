import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { toolchainUpdates } from './lib/toolchains.mjs';

const root = resolve(import.meta.dirname, '..');
const rustPath = resolve(root, 'rust-toolchain.toml');
const dotnetPath = resolve(root, 'global.json');
const nodePath = resolve(root, '.node-version');
const rustSource = readFileSync(rustPath, 'utf8');
const dotnet = JSON.parse(readFileSync(dotnetPath, 'utf8'));
const current = { rust: /channel\s*=\s*"([^"]+)"/.exec(rustSource)[1], dotnet: dotnet.sdk.version, node: readFileSync(nodePath, 'utf8').trim() };
const fetchText = async url => {
  const response = await fetch(url, { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
  return response.text();
};
const [rustManifest, dotnetText, nodeText] = await Promise.all([
  fetchText('https://static.rust-lang.org/dist/channel-rust-stable.toml'),
  fetchText(`https://builds.dotnet.microsoft.com/dotnet/release-metadata/${current.dotnet.split('.').slice(0, 2).join('.')}/releases.json`),
  fetchText('https://nodejs.org/dist/index.json'),
]);
const updates = toolchainUpdates(current, { rustManifest, dotnetRelease: JSON.parse(dotnetText), nodeReleases: JSON.parse(nodeText) });
if (process.argv.includes('--write')) {
  if (updates.rust) writeFileSync(rustPath, rustSource.replace(/channel\s*=\s*"[^"]+"/, `channel = "${updates.rust}"`));
  if (updates.dotnet) { dotnet.sdk.version = updates.dotnet; writeFileSync(dotnetPath, `${JSON.stringify(dotnet, null, 2)}\n`); }
  if (updates.node) writeFileSync(nodePath, `${updates.node}\n`);
}
console.log(JSON.stringify({ applied: process.argv.includes('--write'), current, updates }, null, 2));
