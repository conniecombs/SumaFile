import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';

const crates = ['simplefile-core', 'simplefile-ipc', 'simplefile-service'];
// sumafile-plugin versions on its own, but its packaged manifest requires the simplefile-ipc released with the app.
const pluginCrate = 'crates/sumafile-plugin/Cargo.toml';
const ipcRequirement = /^(simplefile-ipc\s*=\s*\{[^}\n]*\bversion\s*=\s*")([^"]+)(")/m;
// A src-winui project referenced by another project: NuGet writes "SimpleFile.<Name>": "[<version>, )" into the lock
// file of every project that reaches it, and versions it with the app (src-winui/Directory.Build.props). The plugin
// projects (SumaFile.*, Example.*) version on their own and are never rewritten.
const projectReference = /("SimpleFile\.[A-Za-z]+": "\[)([^,\]]+)(, \)")/g;
const skippedFolders = new Set(['.git', 'bin', 'obj', 'node_modules', 'target', 'dist', 'test-results']);

/** Every packages.lock.json in the repository (App, Tests, PluginHost, the plugins, the template, the Hello fixture), sorted. */
export function findNugetLocks(root) {
  const found = [];
  const walk = relative => {
    for (const entry of readdirSync(resolve(root, relative || '.'), { withFileTypes: true })) {
      const child = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isDirectory() && !entry.name.startsWith('.') && !skippedFolders.has(entry.name)) walk(child);
      else if (entry.isFile() && entry.name === 'packages.lock.json') found.push(child);
    }
  };
  walk('');
  return found.sort();
}
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
  const manifest = readFileSync(resolve(root, 'src-winui/SimpleFile.App/app.manifest'), 'utf8');
  if (/<assemblyIdentity version="([^"]+)"/.exec(manifest)?.[1] !== `${version.numeric}.0`) throw new Error('Windows manifest version differs from WinUI');
  const nsis = readFileSync(resolve(root, 'packaging/winui/sumafile-winui.nsi'), 'utf8');
  if (/!define NUMERIC_VERSION "([^"]+)"/.exec(nsis)?.[1] !== version.numeric) throw new Error('NSIS fallback version differs from WinUI');
  const requirement = ipcRequirement.exec(readFileSync(resolve(root, pluginCrate), 'utf8'))?.[2];
  if (requirement !== version.numeric) throw new Error(`sumafile-plugin requires simplefile-ipc ${requirement}, not ${version.numeric}`);
  for (const file of findNugetLocks(root)) {
    for (const [, prefix, locked] of readFileSync(resolve(root, file), 'utf8').matchAll(projectReference)) {
      if (locked !== version.numeric) {
        throw new Error(`${file}: ${prefix.slice(1, prefix.indexOf('"', 1))} is ${locked}, not ${version.numeric}; restore it or run release:prepare`);
      }
    }
  }
  return version;
}

export function readReleaseNotes(root, version = readReleaseVersion(root)) {
  const file = `docs/RELEASE_${version.display}.md`;
  const source = readFileSync(resolve(root, file), 'utf8');
  if (!source.startsWith(`# SumaFile ${version.display} Release `)) throw new Error(`Release notes heading does not match ${version.tag}`);
  return { file, source };
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
  edit(pluginCrate, source => {
    if (!ipcRequirement.test(source)) throw new Error(`${pluginCrate} has no versioned simplefile-ipc dependency`);
    return source.replace(ipcRequirement, `$1${next.numeric}$3`);
  });
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
  edit('src-winui/SimpleFile.App/app.manifest', source => source.replace(/(<assemblyIdentity version=")[^"]+(")/, `$1${next.numeric}.0$2`));
  edit('packaging/winui/sumafile-winui.nsi', source => source.replace(/(!define NUMERIC_VERSION ")[^"]+(")/, `$1${next.numeric}$2`));
  for (const file of findNugetLocks(root)) edit(file, source => source.replace(projectReference, `$1${next.numeric}$3`));
  edit('README.md', source => source
    // shields.io splits a static badge on single dashes: a dash in the text is written "--" and an underscore "__"
    // ("%2D" gives "404 badge not found").
    .replace(/(img\.shields\.io\/badge\/version-).+?(?=-2563eb\))/, `$1${encodeURIComponent(next.display).replaceAll('-', '--').replaceAll('_', '__')}`)
    .replace(/(\| Product version \| `)[^`]+(` \|)/, `$1${next.display}$2`)
    .replaceAll(`SumaFile_${current.display}_`, `SumaFile_${next.display}_`)
    .replace(`SumaFile ${current.display} is Windows-only.`, `SumaFile ${next.display} is Windows-only.`));
  return { version: next, changes };
}
