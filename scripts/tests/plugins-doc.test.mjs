import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// Plan phase 5, Task 6: docs/PLUGINS.md, the author guide, must not fall behind the host. Every name below is read from
// the source of truth (the plugin protocol schema and the manifest reader), never listed here, so a new host method,
// error code, contribution kind or setting type fails this test until the guide documents it.
const root = fileURLToPath(new URL('../..', import.meta.url));
const read = relative => readFileSync(path.join(root, relative), 'utf8');
const guide = () => read('docs/PLUGINS.md');

const schemaRoot = 'ipc/schema/plugin';
const readerPath = 'src-winui/SimpleFile.PluginHost/Manifest/PluginManifestReader.cs';

/** Every protocol JSON file under ipc/schema/plugin, parsed, with its relative path. */
function pluginSchemas() {
  const found = [];
  const walk = relative => {
    for (const entry of readdirSync(path.join(root, relative), { withFileTypes: true })) {
      const child = `${relative}/${entry.name}`;
      if (entry.isDirectory()) walk(child);
      else if (entry.name.endsWith('.json')) found.push({ file: child, json: JSON.parse(read(child)) });
    }
  };
  walk(schemaRoot);
  assert.ok(found.length > 0, `${schemaRoot} holds no protocol JSON file`);
  return found;
}

const unique = values => [...new Set(values)];

function hostMethods() {
  const methods = unique(pluginSchemas().flatMap(({ json }) => (Array.isArray(json.hostMethods) ? json.hostMethods : [])));
  assert.ok(methods.length > 0, `no hostMethods in ${schemaRoot}`);
  for (const method of methods) assert.match(method, /^host\.[a-z_]+$/, `${method} is not a host.* method name`);
  return methods;
}

function archiveEngineMethods() {
  const methods = unique(pluginSchemas().flatMap(({ json }) => Object.keys(json.archiveEngineMethods ?? {})));
  assert.ok(methods.length > 0, `no archiveEngineMethods in ${schemaRoot}`);
  return methods;
}

function notifications() {
  return unique(pluginSchemas().flatMap(({ json }) => Object.keys(json.notifications ?? {})));
}

function errorCodes() {
  const codes = unique(pluginSchemas().flatMap(({ json }) => Object.keys(json.errors ?? {})));
  assert.ok(codes.length > 0, `no errors in ${schemaRoot}`);
  for (const code of codes) assert.match(code, /^-32\d{3}$/, `${code} is not a JSON-RPC error code`);
  return codes;
}

/** The string array a C# field initialises, e.g. `ContributionKinds = ["commands", ...];`. */
function csharpStringArray(source, field) {
  const match = new RegExp(`\\b${field}\\s*=\\s*\\[([^\\]]*)\\]`, 'u').exec(source);
  assert.ok(match, `${readerPath} has no ${field} array`);
  const values = [...match[1].matchAll(/"([^"]+)"/gu)].map(m => m[1]);
  assert.ok(values.length > 0, `${field} in ${readerPath} is empty`);
  return values;
}

const contributionKinds = () => csharpStringArray(read(readerPath), 'ContributionKinds');
const settingTypes = () => csharpStringArray(read(readerPath), 'SettingTypes');

/** The names the guide does not mention as inline code (`name`). */
const missing = (text, names) => names.filter(name => !text.includes(`\`${name}\``));

test('the source lists parse: the schema and the reader still have the shape this test reads', () => {
  assert.ok(hostMethods().includes('host.get_settings'));
  assert.ok(contributionKinds().includes('commands'));
  assert.ok(errorCodes().includes('-32600'));
});

test('PLUGINS.md names every host.* method in ipc/schema/plugin', () => {
  assert.deepEqual(missing(guide(), hostMethods()), []);
});

test('PLUGINS.md names every archive-engine method and notification in ipc/schema/plugin', () => {
  assert.deepEqual(missing(guide(), [...archiveEngineMethods(), ...notifications()]), []);
});

test('PLUGINS.md names every error code in ipc/schema/plugin', () => {
  assert.deepEqual(missing(guide(), errorCodes()), []);
});

test('PLUGINS.md documents every contribution kind the manifest reader accepts', () => {
  const text = guide();
  assert.deepEqual(missing(text, contributionKinds()), []);
  // Each kind also has its own heading in the contributions reference.
  const headings = text.split('\n').filter(line => /^#{2,4} /u.test(line));
  const unheaded = contributionKinds().filter(kind => !headings.some(line => line.includes(`\`${kind}\``)));
  assert.deepEqual(unheaded, [], 'every contribution kind has a heading that names it');
});

test('PLUGINS.md names every declarative setting type the manifest reader accepts', () => {
  assert.deepEqual(missing(guide(), settingTypes()), []);
});

test('the protocol README and the README link the guide', () => {
  assert.match(read(`${schemaRoot}/README.md`), /\]\([^)]*docs\/PLUGINS\.md[^)]*\)/u);
  assert.match(read('README.md'), /\]\(docs\/PLUGINS\.md[^)]*\)/u);
});
