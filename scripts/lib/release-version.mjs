import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const crates = ['simplefile-core', 'simplefile-ipc', 'simplefile-service'];
export const nugetLocks = ['App', 'Core', 'Ipc', 'Tests'].map(name => `src-winui/SimpleFile.${name}/packages.lock.json`);
export function parseVersion(input) {
  const display = input.replace(/^v/, '');
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?$/.exec(display);
  if (!match || match.slice(1, 4).some((part, index) => Number(part) > (index < 2 ? 255 : 65535))) {
    throw new Error('Version must be MAJOR.MINOR.PATCH with an optional prerelease suffix and MSI-compatible numeric fields');
  }
  return { numeric: match.slice(1, 4).join('.'), display, tag: `v${display}`, prerelease: Boolean(match[4]) };
}

export function readReleaseVersion(root) {
  const props = readFileSync(resolve(root, 'src-winui/Directory.Build.props'), 'utf8');
  const version = parseVersion(/<InformationalVersion>([^<]+)<\/InformationalVersion>/.exec(props)?.[1] ?? '');
  if (/<Version>([^<]+)<\/Version>/.exec(props)?.[1] !== version.numeric) throw new Error('WinUI numeric and display versions differ');
  for (const crate of crates) {
    const source = readFileSync(resolve(root, `crates/${crate}/Cargo.toml`), 'utf8');
    if (/^version\s*=\s*"([^"]+)"/m.exec(source)?.[1] !== version.numeric) throw new Error(`${crate} version differs from WinUI`);
  }
  const core = readFileSync(resolve(root, 'crates/simplefile-core/src/lib.rs'), 'utf8');
  if (/APP_DISPLAY_VERSION:\s*&str\s*=\s*"([^"]+)"/.exec(core)?.[1] !== version.display) throw new Error('Rust display version differs from WinUI');
  return version;
}

export function planReleaseVersion(root, input) {
  const next = parseVersion(input);
  const current = readReleaseVersion(root);
  const changes = new Map();
  const edit = (file, transform) => {
    const original = readFileSync(resolve(root, file), 'utf8');
    const updated = transform(original);
    if (original !== updated) changes.set(file, updated);
  };
  for (const crate of crates) edit(`crates/${crate}/Cargo.toml`, source => source.replace(/^version\s*=\s*"[^"]+"/m, `version = "${next.numeric}"`));
  edit('Cargo.lock', source => {
    for (const crate of crates) {
      const pattern = new RegExp(`(name = "${crate}"\\r?\\nversion = ")[^"]+(")`);
      if (!pattern.test(source)) throw new Error(`Cargo.lock has no ${crate} package`);
      source = source.replace(pattern, `$1${next.numeric}$2`);
    }
    return source;
  });
  edit('src-winui/Directory.Build.props', source => source
    .replace(/<Version>[^<]+<\/Version>/, `<Version>${next.numeric}</Version>`)
    .replace(/<InformationalVersion>[^<]+<\/InformationalVersion>/, `<InformationalVersion>${next.display}</InformationalVersion>`));
  edit('crates/simplefile-core/src/lib.rs', source => source.replace(/(APP_DISPLAY_VERSION:\s*&str\s*=\s*")[^"]+(")/, `$1${next.display}$2`));
  for (const file of nugetLocks) edit(file, source => source.replace(
    /("SimpleFile\.(?:Core|Ipc|App|Tests)": "\[)[\d.]+(, \)")/g, `$1${next.numeric}$2`));
  edit('README.md', source => source
    .replace(/(img\.shields\.io\/badge\/version-)[^-]+(?:-[^-]+)*(?=-2563eb\))/, `$1${encodeURIComponent(next.display).replaceAll('-', '%2D')}`)
    .replace(/(\| Product version \| `)[^`]+(` \|)/, `$1${next.display}$2`)
    .replaceAll(`SumaFile_${current.display}_`, `SumaFile_${next.display}_`)
    .replace(`SumaFile ${current.display} is Windows-only.`, `SumaFile ${next.display} is Windows-only.`));
  return { version: next, changes };
}
