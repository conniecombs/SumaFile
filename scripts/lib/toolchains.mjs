export function toolchainUpdates(current, { rustManifest, dotnetRelease, nodeReleases }) {
  const rust = /\[pkg\.rust\][\s\S]*?\nversion\s*=\s*"(\d+\.\d+\.\d+)\s/.exec(rustManifest)?.[1];
  const dotnet = dotnetRelease['latest-sdk'];
  const node = nodeReleases.find(item => item.lts && item.version.startsWith(`v${current.node.split('.')[0]}.`))?.version.slice(1);
  const newer = (candidate, old) => {
    if (!/^\d+\.\d+\.\d+$/.test(candidate ?? '')) throw new Error('Release feed did not contain a stable exact version');
    const a = candidate.split('.').map(Number), b = old.split('.').map(Number);
    for (let index = 0; index < 3; index++) if (a[index] !== b[index]) return a[index] > b[index];
    return false;
  };
  const updates = {};
  for (const [name, version] of Object.entries({ rust, dotnet, node })) {
    if (version?.split('.')[0] !== current[name].split('.')[0]) throw new Error(`${name} major toolchain update requires review`);
    if (newer(version, current[name])) updates[name] = version;
  }
  return updates;
}
