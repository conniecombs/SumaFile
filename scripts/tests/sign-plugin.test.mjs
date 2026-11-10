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
  canonicalPayload, fingerprint, generateSigningKey, publicKeyBase64, signPlugin, verifyPlugin,
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

  // The host checks only listed files, so a file added later keeps the status; verify names it.
  writeFileSync(manifestPath, readFileSync(manifestPath, 'utf8').replace('"Evil"', '"Signed"'));
  signPlugin(folder, keyPath);
  writeFileSync(path.join(folder, 'extra.txt'), 'added after signing');
  state = verifyPlugin(folder);
  assert.equal(state.status, 'Verified');
  assert.deepEqual(state.unlisted, ['extra.txt']);
  assert.match(cli('verify', folder).stdout, /not covered.*extra\.txt/s);
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
