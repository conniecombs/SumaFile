import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, createPrivateKey, sign as edSign } from 'node:crypto';
import {
  existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  MAX_SIGNATURE_BYTES, canonicalPayload, fingerprint, generateSigningKey, publicKeyBase64, signPlugin, verifyPlugin,
} from '../sign-plugin.mjs';

const script = fileURLToPath(new URL('../sign-plugin.mjs', import.meta.url));
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const fixture = path.join(repoRoot, 'src-winui', 'SimpleFile.Tests', 'Fixtures', 'signed-plugin');
// The committed fixture's key fingerprint; PluginTrustTests.SignedFixture_FromTheSigningTool_Verifies expects it too.
const fixtureFingerprint = 'aea57a90c57b3693';

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

function withTemp(run) {
  const dir = mkdtempSync(path.join(tmpdir(), 'sumafile-sign-plugin-'));
  try {
    return run(dir);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

function cli(...args) {
  return spawnSync(process.execPath, [script, ...args], { encoding: 'utf8' });
}

// A plugin folder with a UI assembly, a backend and a nested file, whose manifest names publicKey.
function writePlugin(root, publicKey, extra = {}) {
  const folder = path.join(root, 'com.example.signed');
  mkdirSync(path.join(folder, 'assets', 'icons'), { recursive: true });
  const manifest = {
    id: 'com.example.signed', name: 'Signed', version: '1.0.0', sdkVersion: '1.0.0',
    publisher: { name: 'Example', ...(publicKey === undefined ? {} : { publicKey }) },
    ui: { assembly: 'Example.Plugin.dll', entryType: 'Example.Plugin.ExamplePlugin' },
    backend: { executable: 'example-backend.exe', activateOn: 'first-call' },
    contributions: {},
    ...extra,
  };
  writeFileSync(path.join(folder, 'plugin.json'), JSON.stringify(manifest, null, 2));
  writeFileSync(path.join(folder, 'Example.Plugin.dll'), 'ui assembly bytes');
  writeFileSync(path.join(folder, 'example-backend.exe'), 'backend bytes');
  writeFileSync(path.join(folder, 'assets', 'icons', 'icon.png'), 'png bytes');
  writeFileSync(path.join(folder, 'assets', 'Zeta.txt'), 'zeta');
  return folder;
}

function keyIn(dir) {
  const { keyPath, publicKey } = generateSigningKey(path.join(dir, 'keys'));
  return { keyPath, publicKey, pem: readFileSync(keyPath, 'utf8') };
}

test('keygen writes a PKCS#8 key and prints the raw 32-byte public key', () => withTemp(dir => {
  const out = path.join(dir, 'keys');
  const result = cli('keygen', '--out', out);
  assert.equal(result.status, 0, result.stderr);
  const keyPath = path.join(out, 'signing-key.pem');
  const pem = readFileSync(keyPath, 'utf8');
  assert.match(pem, /^-----BEGIN PRIVATE KEY-----\n/);
  const key = createPrivateKey(pem);
  assert.equal(key.asymmetricKeyType, 'ed25519');
  const publicKey = publicKeyBase64(key);
  assert.equal(Buffer.from(publicKey, 'base64').length, 32);
  assert.match(result.stdout, new RegExp(publicKey.replace(/[+/]/g, '\\$&')));
  assert.match(result.stdout, new RegExp(fingerprint(publicKey)));
  assert.doesNotMatch(result.stdout + result.stderr, /PRIVATE KEY/, 'the private key is never printed');
  if (process.platform !== 'win32') assert.equal(statSync(keyPath).mode & 0o777, 0o600);

  const again = cli('keygen', '--out', out);
  assert.notEqual(again.status, 0, 'an existing key is never overwritten');
  assert.equal(readFileSync(keyPath, 'utf8'), pem);
}));

test('keygen refuses a folder inside a git work tree', () => withTemp(dir => {
  mkdirSync(path.join(dir, 'repo', '.git'), { recursive: true });
  const result = cli('keygen', '--out', path.join(dir, 'repo', 'keys'));
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /git/);
  assert.equal(existsSync(path.join(dir, 'repo', 'keys', 'signing-key.pem')), false);
}));

test('sign then verify round trips, and the payload is the host canonical payload', () => withTemp(dir => {
  const { keyPath, publicKey, pem } = keyIn(dir);
  const folder = writePlugin(dir, publicKey);

  const signed = cli('sign', folder, '--key', keyPath);
  assert.equal(signed.status, 0, signed.stderr);
  const signature = JSON.parse(readFileSync(path.join(folder, 'signature.json'), 'utf8'));
  assert.equal(signature.algorithm, 'ed25519');
  // Every file but plugin.json and signature.json, as forward-slash relative paths, in ordinal order.
  assert.deepEqual(Object.keys(signature.files),
    ['Example.Plugin.dll', 'assets/Zeta.txt', 'assets/icons/icon.png', 'example-backend.exe']);
  for (const [file, hash] of Object.entries(signature.files)) {
    assert.equal(hash, sha256(readFileSync(path.join(folder, ...file.split('/')))));
  }

  // The host's PluginSignatureVerifier.CanonicalPayload, spelled out: plugin.json bytes, "\n", then "<path>\n<hash>\n".
  const manifestBytes = readFileSync(path.join(folder, 'plugin.json'));
  const expected = Buffer.concat([manifestBytes, Buffer.from('\n'),
    ...Object.keys(signature.files).sort().map(file => Buffer.from(`${file}\n${signature.files[file]}\n`, 'utf8'))]);
  assert.deepEqual(canonicalPayload(manifestBytes, signature.files), expected);
  const raw = Buffer.from(signature.signature, 'base64');
  assert.equal(raw.length, 64);
  assert.deepEqual(raw, edSign(null, expected, createPrivateKey(pem)), 'Ed25519 is deterministic');

  const verified = cli('verify', folder);
  assert.equal(verified.status, 0, verified.stderr);
  assert.match(verified.stdout, new RegExp(`Verified.*${fingerprint(publicKey)}`));
  assert.deepEqual(verifyPlugin(folder), { status: 'Verified', fingerprint: fingerprint(publicKey), error: null, unlisted: [] });
}));

test('a changed, missing or added file is caught', () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  const folder = writePlugin(dir, publicKey);
  signPlugin(folder, keyPath);

  writeFileSync(path.join(folder, 'assets', 'Zeta.txt'), 'changed');
  let state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.match(state.error, /"assets\/Zeta.txt" does not match its recorded hash/);
  assert.notEqual(cli('verify', folder).status, 0);

  signPlugin(folder, keyPath);
  rmSync(path.join(folder, 'assets', 'Zeta.txt'));
  assert.match(verifyPlugin(folder).error, /"assets\/Zeta.txt" is missing/);

  signPlugin(folder, keyPath);
  const manifestPath = path.join(folder, 'plugin.json');
  writeFileSync(manifestPath, readFileSync(manifestPath, 'utf8').replace('"Signed"', '"Evil"'));
  state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.match(state.error, /Signature does not match the package contents/);

  // The host rejects any file signature.json does not list (plugin.json and signature.json aside), so a file added
  // after signing makes the folder Invalid, wherever it is.
  writeFileSync(manifestPath, readFileSync(manifestPath, 'utf8').replace('"Evil"', '"Signed"'));
  signPlugin(folder, keyPath);
  writeFileSync(path.join(folder, 'extra.txt'), 'added after signing');
  state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.match(state.error, /"extra\.txt" is in the plugin folder but not listed in signature\.json files/);
  assert.deepEqual(state.unlisted, ['extra.txt']);
  const result = cli('verify', folder);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /Invalid: .*extra\.txt/);

  rmSync(path.join(folder, 'extra.txt'));
  mkdirSync(path.join(folder, 'assets', 'new'));
  writeFileSync(path.join(folder, 'assets', 'new', 'Late.dll'), 'nested, added after signing');
  writeFileSync(path.join(folder, '.hidden'), 'hidden files count too');
  state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.deepEqual(state.unlisted, ['.hidden', 'assets/new/Late.dll']);
  assert.match(state.error, /^"\.hidden" and 1 other files in the plugin folder are not listed in signature\.json files\.$/);
  assert.equal(state.fingerprint, fingerprint(publicKey), 'the signature itself checked out');

  // An empty folder holds no file, and re-signing covers the new files.
  rmSync(path.join(folder, '.hidden'));
  rmSync(path.join(folder, 'assets', 'new', 'Late.dll'));
  assert.equal(verifyPlugin(folder).status, 'Verified');
  writeFileSync(path.join(folder, 'assets', 'new', 'Late.dll'), 'now signed');
  signPlugin(folder, keyPath);
  assert.deepEqual(verifyPlugin(folder), { status: 'Verified', fingerprint: fingerprint(publicKey), error: null, unlisted: [] });
}));

test('signature.json may start with a UTF-8 BOM and is capped at 1 MiB', () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  const folder = writePlugin(dir, publicKey);
  signPlugin(folder, keyPath);
  const signaturePath = path.join(folder, 'signature.json');
  const text = readFileSync(signaturePath, 'utf8');
  writeFileSync(signaturePath, `\uFEFF${text}`);
  assert.equal(verifyPlugin(folder).status, 'Verified', 'the host skips a BOM, and so does the tool');

  // Padded past the cap with whitespace, which JSON allows: the host never reads it.
  writeFileSync(signaturePath, text + ' '.repeat(MAX_SIGNATURE_BYTES - Buffer.byteLength(text) + 1));
  let state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.match(state.error, /signature\.json is larger than 1 MiB/);
  writeFileSync(signaturePath, text + ' '.repeat(MAX_SIGNATURE_BYTES - Buffer.byteLength(text)));
  assert.equal(verifyPlugin(folder).status, 'Verified', 'exactly 1 MiB is still read');
  assert.equal(MAX_SIGNATURE_BYTES, 1024 * 1024);
}));

test('sign refuses to write a signature.json over 1 MiB', () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  const folder = writePlugin(dir, publicKey);
  // Enough files that their listing alone passes 1 MiB (about 190 bytes an entry).
  const many = path.join(folder, 'many');
  mkdirSync(many);
  const name = index => `${String(index).padStart(5, '0')}-${'x'.repeat(100)}.txt`;
  for (let index = 0; index < 6000; index += 1) writeFileSync(path.join(many, name(index)), '');
  assert.throws(() => signPlugin(folder, keyPath), /signature\.json would be \d+ bytes, larger than the 1 MiB the host reads/);
  assert.equal(existsSync(path.join(folder, 'signature.json')), false, 'nothing is written');
}));

test('publisher.publicKey is compared by its decoded 32 bytes, not its text', () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  // The host decodes publicKey with Convert.FromBase64String, which skips whitespace.
  const spaced = ` ${publicKey.slice(0, 20)}\n${publicKey.slice(20)} `;
  const folder = writePlugin(dir, spaced);
  const signed = signPlugin(folder, keyPath);
  assert.equal(signed.fingerprint, fingerprint(publicKey));
  assert.equal(verifyPlugin(folder).status, 'Verified');
  rmSync(folder, { recursive: true });

  for (const [declared, error] of [
    ['not base64!', /publisher\.publicKey in plugin\.json is not valid base64/],
    [Buffer.alloc(16).toString('base64'), /publisher\.publicKey in plugin\.json must be exactly 32 bytes/],
  ]) {
    const bad = writePlugin(dir, declared);
    assert.throws(() => signPlugin(bad, keyPath), error);
    assert.equal(existsSync(path.join(bad, 'signature.json')), false);
    rmSync(bad, { recursive: true });
  }
}));

test('a mismatched or missing publisher.publicKey is refused', () => withTemp(dir => {
  const { keyPath } = keyIn(dir);
  const other = generateSigningKey(path.join(dir, 'other'));
  const folder = writePlugin(dir, other.publicKey);
  assert.throws(() => signPlugin(folder, keyPath), /publisher\.publicKey .*does not match/);
  const result = cli('sign', folder, '--key', keyPath);
  assert.notEqual(result.status, 0);
  assert.equal(existsSync(path.join(folder, 'signature.json')), false, 'nothing is written');

  rmSync(folder, { recursive: true });
  const unkeyed = writePlugin(dir, undefined);
  assert.throws(() => signPlugin(unkeyed, keyPath), /publisher\.publicKey/);
}));

test('the declared UI assembly and backend must be in the folder (the host rule)', () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  for (const file of ['Example.Plugin.dll', 'example-backend.exe']) {
    const folder = writePlugin(dir, publicKey);
    rmSync(path.join(folder, file));
    assert.throws(() => signPlugin(folder, keyPath), new RegExp(`"${file.replace('.', '\\.')}" is declared`));
    rmSync(folder, { recursive: true });
  }

  // A signature.json that leaves the declared code out is Invalid, as the host rules.
  const folder = writePlugin(dir, publicKey);
  signPlugin(folder, keyPath);
  const signature = JSON.parse(readFileSync(path.join(folder, 'signature.json'), 'utf8'));
  delete signature.files['example-backend.exe'];
  const payload = canonicalPayload(readFileSync(path.join(folder, 'plugin.json')), signature.files);
  signature.signature = edSign(null, payload, createPrivateKey(readFileSync(keyPath))).toString('base64');
  writeFileSync(path.join(folder, 'signature.json'), JSON.stringify(signature));
  const state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.match(state.error, /"example-backend\.exe" is declared by the manifest but not listed/);
}));

test('symlinks and paths the host cannot open are refused', { skip: process.platform === 'win32' }, () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  let folder = writePlugin(dir, publicKey);
  symlinkSync(path.join(folder, 'assets', 'Zeta.txt'), path.join(folder, 'link.txt'));
  assert.throws(() => signPlugin(folder, keyPath), /symbolic link/);
  rmSync(folder, { recursive: true });

  folder = writePlugin(dir, publicKey);
  writeFileSync(path.join(folder, 'back\\slash.txt'), 'x');
  assert.throws(() => signPlugin(folder, keyPath), /cannot be listed/);
  rmSync(folder, { recursive: true });

  folder = writePlugin(dir, publicKey);
  writeFileSync(path.join(folder, 'assets', 'zeta.txt'), 'differs only in case');
  assert.throws(() => signPlugin(folder, keyPath), /differ only in case/);
  rmSync(folder, { recursive: true });

  // A link added after signing is not listed, so verify reports the folder Invalid, as the host does.
  folder = writePlugin(dir, publicKey);
  signPlugin(folder, keyPath);
  symlinkSync(path.join(folder, 'assets', 'Zeta.txt'), path.join(folder, 'late-link.txt'));
  let state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.match(state.error, /"late-link\.txt" is in the plugin folder/);
  rmSync(path.join(folder, 'late-link.txt'));
  // A link to a folder is never followed, and is itself an unlisted entry.
  symlinkSync(path.join(folder, 'assets'), path.join(folder, 'linked-assets'));
  state = verifyPlugin(folder);
  assert.equal(state.status, 'Invalid');
  assert.deepEqual(state.unlisted, ['linked-assets']);
}));

test('verify follows the host on malformed signature files', () => withTemp(dir => {
  const { keyPath, publicKey } = keyIn(dir);
  const folder = writePlugin(dir, publicKey);
  assert.equal(verifyPlugin(folder).status, 'Unsigned');
  assert.notEqual(cli('verify', folder).status, 0, 'an unsigned folder does not verify');
  signPlugin(folder, keyPath);
  const good = JSON.parse(readFileSync(path.join(folder, 'signature.json'), 'utf8'));
  const cases = [
    [{ ...good, algorithm: 'rsa' }, /algorithm must be "ed25519"/],
    [{ ...good, files: [] }, /missing a "files" object/],
    [{ ...good, files: { ...good.files, 'plugin.json': sha256(Buffer.from('x')) } }, /"plugin.json" must not be listed/],
    [{ ...good, files: { ...good.files, '../outside.txt': sha256(Buffer.from('x')) } }, /not a bare relative path/],
    [{ ...good, signature: 'AAAA' }, /signature must be exactly 64 bytes/],
    [{ ...good, signature: 'not base64!' }, /signature is not valid base64/],
  ];
  for (const [content, error] of cases) {
    writeFileSync(path.join(folder, 'signature.json'), JSON.stringify(content));
    const state = verifyPlugin(folder);
    assert.equal(state.status, 'Invalid', JSON.stringify(content));
    assert.match(state.error, error);
  }
}));

test('the committed fixture was signed by this tool and verifies', () => {
  // The C# host verifies the same folder (PluginTrustTests.SignedFixture_FromTheSigningTool_Verifies).
  const state = verifyPlugin(fixture);
  assert.deepEqual(state, { status: 'Verified', fingerprint: fixtureFingerprint, error: null, unlisted: [] });
  // Single-line files: a Windows checkout's CRLF conversion (`* text=auto`) has no line ending to change, so the
  // hashed bytes are the same on every platform.
  for (const file of ['plugin.json', 'payload.txt']) {
    assert.doesNotMatch(readFileSync(path.join(fixture, file), 'latin1'), /[\r\n]/, `${file} has no line ending`);
  }
  assert.equal(existsSync(path.join(fixture, 'signing-key.pem')), false, 'only public material is committed');
});

test('npm run sign:plugin runs the tool', () => {
  const scripts = JSON.parse(readFileSync(path.join(repoRoot, 'package.json'), 'utf8')).scripts;
  assert.equal(scripts['sign:plugin'], 'node scripts/sign-plugin.mjs');
  const usage = cli();
  assert.equal(usage.status, 2);
  assert.match(usage.stderr, /keygen --out <dir>/);
});

test('git ignores plugin signing keys anywhere in the repository', () => {
  // `git check-ignore` exits 0 for an ignored path; nothing needs to exist.
  for (const file of ['signing-key.pem', 'plugins/example/keys/signing-key.pem', 'my-publisher.pem']) {
    const result = spawnSync('git', ['check-ignore', '--no-index', '-q', file], { cwd: repoRoot });
    assert.equal(result.status, 0, `${file} is ignored`);
  }
});
