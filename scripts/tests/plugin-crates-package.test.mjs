import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parseWorkflow } from '../lib/workflow-policy.mjs';

// Plan phase 5, Task 3: the plugin helper crate and the IPC crate it depends on are packaged together as a dry run
// (`cargo package`, which also builds the packaged sources). Nothing is published.
const root = fileURLToPath(new URL('../..', import.meta.url));
const read = relative => readFileSync(path.join(root, relative), 'utf8');
const crates = ['simplefile-ipc', 'sumafile-plugin'];

// The [section] of a Cargo.toml as key -> raw value text. Only single-line `key = value` entries, which is all these
// manifests use; inline tables stay raw text.
function section(source, name) {
  const lines = source.split(/\r?\n/);
  const start = lines.findIndex(line => line.trim() === `[${name}]`);
  if (start < 0) return null;
  const entries = {};
  for (const line of lines.slice(start + 1)) {
    if (/^\s*\[/.test(line)) break;
    const match = /^\s*([A-Za-z0-9_-]+)\s*=\s*(.+?)\s*$/.exec(line);
    if (match) entries[match[1]] = match[2];
  }
  return entries;
}

const string = raw => {
  const match = /^"([^"]*)"$/.exec(raw ?? '');
  return match ? match[1] : null;
};
const array = raw => {
  const match = /^\[(.*)\]$/.exec(raw ?? '');
  if (!match) return null;
  return [...match[1].matchAll(/"([^"]*)"/g)].map(item => item[1]);
};
const semver = text => {
  const match = /^(\d+)\.(\d+)(?:\.(\d+))?$/.exec(text ?? '');
  return match ? [Number(match[1]), Number(match[2]), Number(match[3] ?? 0)] : null;
};
const compare = (a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2];

// A subset of the crates.io category slugs; a slug outside the registry's list fails the real upload, so the
// manifests only use these.
const knownCategories = new Set([
  'api-bindings', 'asynchronous', 'development-tools', 'encoding', 'filesystem', 'network-programming', 'os',
  'os::windows-apis',
]);

const manifest = crate => read(`crates/${crate}/Cargo.toml`);
const pkg = crate => section(manifest(crate), 'package');

for (const crate of crates) {
  test(`${crate} declares the metadata a crates.io package needs`, () => {
    const fields = pkg(crate);
    assert.ok(fields, `crates/${crate}/Cargo.toml has a [package] section`);
    assert.equal(string(fields.name), crate);
    for (const key of ['version', 'description', 'repository', 'edition']) {
      assert.ok(string(fields[key]), `${crate}: ${key} is required`);
    }
    assert.equal(string(fields.license), 'MIT', `${crate}: license = "MIT"`);
    assert.equal(fields.publish, undefined, `${crate}: a packaged crate must not set publish`);

    assert.equal(string(fields.readme), 'README.md', `${crate}: readme = "README.md"`);
    const readme = path.join(root, 'crates', crate, 'README.md');
    assert.ok(existsSync(readme), `crates/${crate}/README.md exists`);
    const readmeText = readFileSync(readme, 'utf8');
    assert.match(readmeText, new RegExp(`^# ${crate}\\b`, 'm'), `${crate}: README starts with the crate name`);
    assert.match(readmeText, /SumaFile/, `${crate}: README names the product`);

    const keywords = array(fields.keywords);
    assert.ok(keywords && keywords.length >= 1 && keywords.length <= 5, `${crate}: 1 to 5 keywords`);
    for (const keyword of keywords) {
      assert.match(keyword, /^[a-z][a-z0-9_+-]{0,19}$/, `${crate}: keyword ${keyword} is a crates.io keyword`);
    }
    const categories = array(fields.categories);
    assert.ok(categories && categories.length >= 1 && categories.length <= 5, `${crate}: 1 to 5 categories`);
    for (const category of categories) assert.ok(knownCategories.has(category), `${crate}: unknown category ${category}`);
  });

  test(`${crate} declares a rust-version the pinned toolchain satisfies`, () => {
    const channel = semver(/^\s*channel\s*=\s*"([^"]+)"/m.exec(read('rust-toolchain.toml'))?.[1]);
    assert.ok(channel, 'rust-toolchain.toml pins a numeric channel');
    const rustVersion = semver(string(pkg(crate)['rust-version']));
    assert.ok(rustVersion, `${crate}: rust-version is a numeric version`);
    // Taken from the channel when it was set. A newer toolchain keeps satisfying it; an older one would not build.
    assert.ok(compare(rustVersion, channel) <= 0, `${crate}: rust-version must not be newer than the toolchain channel`);
  });
}

test('sumafile-plugin names a simplefile-ipc version the path crate satisfies', () => {
  const dependencies = section(manifest('sumafile-plugin'), 'dependencies');
  const raw = dependencies['simplefile-ipc'];
  assert.ok(raw, 'sumafile-plugin depends on simplefile-ipc');
  assert.match(raw, /path\s*=\s*"\.\.\/simplefile-ipc"/, 'the dependency stays a path dependency in the workspace');
  // A packaged crate cannot keep a path; cargo package replaces it with this registry requirement.
  const requirement = /version\s*=\s*"([^"]+)"/.exec(raw)?.[1];
  assert.ok(requirement, 'the path dependency also names a version, which cargo package needs');
  const wanted = semver(requirement);
  assert.ok(wanted, `simplefile-ipc requirement ${requirement} is a plain caret version`);
  const actual = semver(string(pkg('simplefile-ipc').version));
  // Caret semantics for a 1.x requirement: same major, not older. A simplefile-ipc release bump (release-version.mjs)
  // stays compatible; a new major needs this requirement raised with it.
  assert.equal(actual[0], wanted[0], 'simplefile-ipc keeps the major version sumafile-plugin requires');
  assert.ok(compare(actual, wanted) >= 0, 'simplefile-ipc is not older than the version sumafile-plugin requires');
});

const packagesBothCrates = command => /\bcargo package\b/.test(command)
  && /-p simplefile-ipc\b/.test(command) && /-p sumafile-plugin\b/.test(command);

test('npm run pack:crates packages both crates as a verified dry run', () => {
  const script = JSON.parse(read('package.json')).scripts['pack:crates'];
  assert.ok(script, 'package.json has a pack:crates script');
  assert.ok(packagesBothCrates(script), 'pack:crates packages simplefile-ipc and sumafile-plugin together');
  assert.match(script, /--locked\b/, 'pack:crates keeps Cargo.lock as it is');
  assert.doesNotMatch(script, /--no-verify\b/, 'the dry run must also build the packaged sources');
  assert.doesNotMatch(script, /\bpublish\b/, 'pack:crates never publishes');
});

test('the Rust quality gate packages the plugin crates after its tests', () => {
  const quality = parseWorkflow(read('.github/workflows/ci.yml')).jobs.quality;
  const names = quality.steps.map(step => step.name);
  const index = names.indexOf('Package the plugin crates (dry run)');
  assert.ok(index >= 0, 'ci.yml quality has a "Package the plugin crates (dry run)" step');
  assert.ok(index > names.indexOf('Run tests'), 'the package step runs after the tests');
  const step = quality.steps[index];
  assert.ok(packagesBothCrates(step.run), 'the CI step packages both crates');
  assert.match(step.run, /--locked\b/);
  assert.doesNotMatch(step.run, /--no-verify\b|--allow-dirty\b/, 'CI packages the clean checkout and builds it');
  assert.equal(step.if, quality.steps[names.indexOf('Run tests')].if, 'gated like the tests');
});

test('no workflow or npm script publishes a crate or a NuGet package', () => {
  const sources = [read('package.json')];
  for (const name of readdirSync(path.join(root, '.github/workflows'))) {
    if (/\.ya?ml$/.test(name)) sources.push(read(`.github/workflows/${name}`));
  }
  for (const source of sources) {
    assert.doesNotMatch(source, /\bcargo publish\b|\bdotnet nuget push\b|\bnuget push\b/);
  }
});
