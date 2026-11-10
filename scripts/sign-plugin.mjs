#!/usr/bin/env node
// Signs a SumaFile plugin folder with Ed25519 and checks the result the way the host does.
//
//   node scripts/sign-plugin.mjs keygen --out <dir>          writes <dir>/signing-key.pem, prints publisher.publicKey
//   node scripts/sign-plugin.mjs sign <plugin folder> --key <pem>    writes <plugin folder>/signature.json
//   node scripts/sign-plugin.mjs verify <plugin folder>      re-checks signature.json, exit 0 only when Verified
//
// The format is the host's (src-winui/SimpleFile.PluginHost/Trust/PluginSignatureVerifier.cs, spec §9):
// signature.json is { "algorithm": "ed25519", "files": { "<path>": "<sha256 hex>" }, "signature": "<base64>" }, at
// most 1 MiB, optionally after a UTF-8 BOM. The files are every file of the folder except plugin.json and
// signature.json, as bare forward-slash relative paths, and a file the list leaves out makes the folder Invalid. The
// signature is Ed25519 over the canonical payload: the plugin.json bytes, "\n", then "<path>\n<hash>\n" per file in
// ordinal path order. publisher.publicKey in plugin.json is the raw 32-byte public key in base64, and the key
// fingerprint the host shows is the first 16 hex characters of its SHA-256.
//
// The private key never belongs in a repository: keygen refuses a folder inside a git work tree.

import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify } from 'node:crypto';
import {
  closeSync, existsSync, lstatSync, mkdirSync, openSync, readFileSync, readdirSync, statSync, writeFileSync, writeSync,
} from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const KEY_FILE = 'signing-key.pem';
// PluginManifestReader.MaxManifestBytes: the host never reads a larger plugin.json.
export const MAX_MANIFEST_BYTES = 1024 * 1024;
// The host's cap on signature.json: a larger one is Invalid, so sign never writes one.
export const MAX_SIGNATURE_BYTES = 1024 * 1024;
const EXCLUDED = new Set(['plugin.json', 'signature.json']);

// File.Exists: a file, following links; false for a folder or a missing path.
const fileExists = file => {
  try {
    return statSync(file).isFile();
  } catch {
    return false;
  }
};
const sha256Hex = bytes => createHash('sha256').update(bytes).digest('hex');
// StringComparer.Ordinal: UTF-16 code units, which is how JavaScript compares strings.
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

/** The raw 32-byte Ed25519 public key of a key object (private or public), in base64. */
export function publicKeyBase64(key) {
  const jwk = createPublicKey(key).export({ format: 'jwk' });
  if (jwk.kty !== 'OKP' || jwk.crv !== 'Ed25519') throw new Error('The key is not an Ed25519 key.');
  return Buffer.from(jwk.x, 'base64url').toString('base64');
}

/** The host's key fingerprint: the first 16 lowercase hex characters of SHA-256 over the raw public key. */
export function fingerprint(publicKey) {
  return sha256Hex(Buffer.from(publicKey, 'base64')).slice(0, 16);
}

/** PluginSignatureVerifier.CanonicalPayload: plugin.json bytes, "\n", then "<path>\n<hash>\n" per file, ordinal order. */
export function canonicalPayload(manifestBytes, files) {
  const parts = [Buffer.from(manifestBytes), Buffer.from('\n')];
  for (const file of Object.keys(files).sort(ordinal)) parts.push(Buffer.from(`${file}\n${files[file]}\n`, 'utf8'));
  return Buffer.concat(parts);
}

function insideGitWorkTree(dir) {
  for (let current = path.resolve(dir); ; current = path.dirname(current)) {
    if (existsSync(path.join(current, '.git'))) return current;
    if (path.dirname(current) === current) return null;
  }
}

/** Writes <outDir>/signing-key.pem (PKCS#8, mode 0600 where the OS has modes) and returns its public key. */
export function generateSigningKey(outDir, { allowRepository = false } = {}) {
  const repository = insideGitWorkTree(outDir);
  if (repository && !allowRepository) {
    throw new Error(`${path.resolve(outDir)} is inside the git work tree ${repository}. Keep the private key out of `
      + 'every repository: choose a folder outside it (or pass --allow-repository if it is ignored and never pushed).');
  }
  const { privateKey } = generateKeyPairSync('ed25519');
  const pem = privateKey.export({ format: 'pem', type: 'pkcs8' });
  mkdirSync(outDir, { recursive: true, mode: 0o700 });
  const keyPath = path.join(outDir, KEY_FILE);
  // 'wx': an existing key is never overwritten, so a publisher's key cannot be lost to a second keygen.
  let fd;
  try {
    fd = openSync(keyPath, 'wx', 0o600);
  } catch (error) {
    if (error.code === 'EEXIST') throw new Error(`${keyPath} already exists; it is never overwritten.`);
    throw error;
  }
  try {
    writeSync(fd, pem);
  } finally {
    closeSync(fd);
  }
  return { keyPath, publicKey: publicKeyBase64(privateKey) };
}

function readManifest(folder) {
  const manifestPath = path.join(folder, 'plugin.json');
  if (!existsSync(manifestPath)) throw new Error(`${folder} has no plugin.json.`);
  const bytes = readFileSync(manifestPath);
  if (bytes.length > MAX_MANIFEST_BYTES) throw new Error('plugin.json is larger than 1 MiB.');
  let manifest;
  try {
    manifest = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
  } catch (error) {
    throw new Error(`plugin.json is not valid JSON: ${error.message}`);
  }
  if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest)) throw new Error('plugin.json must be a JSON object.');
  return { bytes, manifest };
}

// The code files whose coverage makes "Verified" speak for what runs: ui.assembly and backend.executable.
function declaredCode(manifest) {
  return [manifest.ui?.assembly, manifest.backend?.executable].filter(code => typeof code === 'string' && code !== '');
}

// PluginSignatureVerifier.IsBareRelativePath, plus the characters a Windows path cannot hold, so the host can open
// every listed file.
function isBareRelativePath(file) {
  return file !== '' && !file.startsWith('/') && !/[\\:|<>"?*\u0000-\u001f]/.test(file)
    && file.split('/').every(segment => segment !== '' && segment !== '.' && segment !== '..');
}

/** Every file of the folder except plugin.json and signature.json, as forward-slash relative paths, ordinal order. */
export function listPluginFiles(folder) {
  const files = [];
  const walk = relative => {
    for (const entry of readdirSync(path.join(folder, ...relative), { withFileTypes: true })) {
      const parts = [...relative, entry.name];
      const file = parts.join('/');
      const stat = lstatSync(path.join(folder, ...parts));
      if (stat.isSymbolicLink()) throw new Error(`"${file}" is a symbolic link; a signed plugin holds plain files only.`);
      if (stat.isDirectory()) {
        walk(parts);
      } else if (stat.isFile()) {
        if (relative.length === 0 && EXCLUDED.has(entry.name)) continue;
        if (!isBareRelativePath(file)) throw new Error(`"${file}" cannot be listed: the host opens bare relative paths only.`);
        files.push(file);
      } else {
        throw new Error(`"${file}" is not a regular file.`);
      }
    }
  };
  walk([]);
  files.sort(ordinal);
  const seen = new Map();
  for (const file of files) {
    const folded = file.toLowerCase();
    if (seen.has(folded)) throw new Error(`"${seen.get(folded)}" and "${file}" differ only in case; Windows cannot hold both.`);
    seen.set(folded, file);
  }
  return files;
}

/** Signs the folder with the PKCS#8 key at keyPath and writes signature.json. Returns what was written. */
export function signPlugin(folder, keyPath) {
  const privateKey = createPrivateKey(readFileSync(keyPath));
  if (privateKey.asymmetricKeyType !== 'ed25519') throw new Error(`${keyPath} is not an Ed25519 private key.`);
  const publicKey = publicKeyBase64(privateKey);
  const { bytes, manifest } = readManifest(folder);
  const declared = manifest.publisher?.publicKey;
  if (typeof declared !== 'string' || declared.trim() === '') {
    throw new Error(`plugin.json has no publisher.publicKey; set it to "${publicKey}", the key's public key.`);
  }
  // The host decodes publicKey (Convert.FromBase64String, which skips whitespace) and checks the 32 bytes, so the
  // key bytes are compared, not the text.
  const declaredBytes = strictBase64(declared);
  if (!declaredBytes) throw new Error(`publisher.publicKey in plugin.json is not valid base64; set it to "${publicKey}".`);
  if (declaredBytes.length !== 32) {
    throw new Error(`publisher.publicKey in plugin.json must be exactly 32 bytes; set it to "${publicKey}".`);
  }
  if (!declaredBytes.equals(Buffer.from(publicKey, 'base64'))) {
    throw new Error(`publisher.publicKey in plugin.json does not match the signing key (its public key is "${publicKey}").`);
  }

  const listed = listPluginFiles(folder);
  for (const code of declaredCode(manifest)) {
    if (!listed.some(file => file.toLowerCase() === code.toLowerCase())) {
      throw new Error(`"${code}" is declared by the manifest but is not in ${folder}; sign the folder that ships.`);
    }
  }
  const files = {};
  for (const file of listed) files[file] = sha256Hex(readFileSync(path.join(folder, ...file.split('/'))));
  const signature = sign(null, canonicalPayload(bytes, files), privateKey).toString('base64');
  const document = { algorithm: 'ed25519', files, signature };
  const text = `${JSON.stringify(document, null, 2)}\n`;
  const size = Buffer.byteLength(text, 'utf8');
  if (size > MAX_SIGNATURE_BYTES) {
    throw new Error(`signature.json would be ${size} bytes, larger than the 1 MiB the host reads; ship fewer files.`);
  }
  writeFileSync(path.join(folder, 'signature.json'), text);
  return { ...document, fingerprint: fingerprint(publicKey) };
}

// Convert.FromBase64String: the base64 alphabet with correct padding; whitespace is ignored.
function strictBase64(text) {
  const compact = text.replace(/[ \t\r\n]/g, '');
  if (compact.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(compact)) return null;
  return Buffer.from(compact, 'base64');
}

/**
 * PluginSignatureVerifier.Unlisted: every entry under the folder, hidden ones included, that is neither a listed file
 * nor the root plugin.json or signature.json, as forward-slash relative paths in ordinal order. Folders count only when
 * they are links (never followed); a link to a file counts as a file. Paths compare case-insensitively on Windows, as
 * its file system does, and ordinally elsewhere. An unreadable folder throws.
 */
function unlistedEntries(folder, listed) {
  const fold = process.platform === 'win32' ? file => file.toLowerCase() : file => file;
  const covered = new Set([...listed, ...EXCLUDED].map(fold));
  const unlisted = [];
  const walk = relative => {
    for (const entry of readdirSync(path.join(folder, ...relative), { withFileTypes: true })) {
      const parts = [...relative, entry.name];
      const file = parts.join('/');
      if (entry.isDirectory()) {
        walk(parts);
      } else if (!covered.has(fold(file)) || (entry.isSymbolicLink() && isFolderLink(path.join(folder, ...parts)))) {
        unlisted.push(file);
      }
    }
  };
  walk([]);
  return unlisted.sort(ordinal);
}

const isFolderLink = full => {
  try {
    return statSync(full).isDirectory();
  } catch {
    return false;
  }
};

/**
 * PluginSignatureVerifier.Verify, check for check: { status: 'Unsigned' | 'Verified' | 'Invalid', fingerprint, error,
 * unlisted }. unlisted names the files of the folder signature.json does not list; any one makes the folder Invalid,
 * as the host rules.
 */
export function verifyPlugin(folder) {
  const result = (status, error, keyFingerprint = null, unlisted = []) => ({ status, fingerprint: keyFingerprint, error, unlisted });
  const invalid = error => result('Invalid', error);
  const signaturePath = path.join(folder, 'signature.json');
  if (!existsSync(signaturePath)) return result('Unsigned', null);
  let root;
  let manifestBytes;
  let manifest;
  try {
    if (statSync(signaturePath).size > MAX_SIGNATURE_BYTES) return invalid('signature.json is larger than 1 MiB.');
    // A UTF-8 BOM before the JSON is skipped, as the host skips it.
    root = JSON.parse(readFileSync(signaturePath, 'utf8').replace(/^\uFEFF/, ''));
    ({ bytes: manifestBytes, manifest } = readManifest(folder));
  } catch (error) {
    return invalid(`Could not verify signature: ${error.message}`);
  }
  if (!root || typeof root !== 'object' || Array.isArray(root)) return invalid('signature.json must be a JSON object.');
  if (root.algorithm !== 'ed25519') return invalid('signature.json algorithm must be "ed25519".');
  if (!root.files || typeof root.files !== 'object' || Array.isArray(root.files)) return invalid('signature.json is missing a "files" object.');
  for (const [file, hash] of Object.entries(root.files)) {
    if (typeof hash !== 'string') return invalid(`Hash for "${file}" must be a string.`);
  }
  if (typeof root.signature !== 'string') return invalid('signature.json is missing a "signature" string.');

  const publicKey = manifest.publisher?.publicKey;
  if (typeof publicKey !== 'string' || publicKey.trim() === '') {
    return invalid('signature.json is present but the manifest has no publisher.publicKey.');
  }
  const pub = strictBase64(publicKey);
  if (!pub) return invalid('publisher.publicKey is not valid base64.');
  if (pub.length !== 32) return invalid('publisher.publicKey must be exactly 32 bytes.');
  const sig = strictBase64(root.signature);
  if (!sig) return invalid('signature is not valid base64.');
  if (sig.length !== 64) return invalid('signature must be exactly 64 bytes.');

  const listed = Object.keys(root.files);
  for (const code of declaredCode(manifest)) {
    if (!fileExists(path.join(folder, code))) continue;
    if (!listed.some(file => file.toLowerCase() === code.toLowerCase())) {
      return invalid(`"${code}" is declared by the manifest but not listed in signature.json files.`);
    }
  }

  const keyFingerprint = sha256Hex(pub).slice(0, 16);
  for (const [file, expected] of Object.entries(root.files)) {
    if (!isBareRelativePath(file)) return invalid(`Listed path "${file}" is not a bare relative path.`);
    if (EXCLUDED.has(file)) return invalid(`"${file}" must not be listed in files.`);
    const full = path.join(folder, ...file.split('/'));
    if (!fileExists(full)) return invalid(`Listed file "${file}" is missing.`);
    if (sha256Hex(readFileSync(full)) !== expected.toLowerCase()) return invalid(`Listed file "${file}" does not match its recorded hash.`);
  }

  const key = createPublicKey({ key: { kty: 'OKP', crv: 'Ed25519', x: pub.toString('base64url') }, format: 'jwk' });
  if (!verify(null, canonicalPayload(manifestBytes, root.files), key, sig)) {
    return result('Invalid', 'Signature does not match the package contents.', keyFingerprint);
  }

  // Verified must speak for every file the plugin can load, not only the listed ones: an unlisted file (or a link to a
  // folder) beside the signed ones makes the folder Invalid, as the host rules.
  let unlisted;
  try {
    unlisted = unlistedEntries(folder, listed);
  } catch (error) {
    return invalid(`Could not verify signature: ${error.message}`);
  }
  if (unlisted.length === 1) {
    return result('Invalid', `"${unlisted[0]}" is in the plugin folder but not listed in signature.json files.`, keyFingerprint, unlisted);
  }
  if (unlisted.length > 1) {
    return result('Invalid', `"${unlisted[0]}" and ${unlisted.length - 1} other files in the plugin folder are not listed in `
      + 'signature.json files.', keyFingerprint, unlisted);
  }
  return result('Verified', null, keyFingerprint);
}

const usage = `Usage:
  node scripts/sign-plugin.mjs keygen --out <dir> [--allow-repository]
  node scripts/sign-plugin.mjs sign <plugin folder> --key <signing-key.pem>
  node scripts/sign-plugin.mjs verify <plugin folder>`;

function parseArgs(argv) {
  const positional = [];
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--allow-repository') {
      options.allowRepository = true;
    } else if (arg === '--out' || arg === '--key') {
      if (index + 1 >= argv.length) throw new UsageError(`${arg} needs a value.`);
      options[arg.slice(2)] = argv[++index];
    } else if (arg.startsWith('--')) {
      throw new UsageError(`Unknown option ${arg}.`);
    } else {
      positional.push(arg);
    }
  }
  return { positional, options };
}

class UsageError extends Error {}

function main(argv) {
  const [command, ...rest] = argv;
  const { positional, options } = parseArgs(rest);
  if (command === 'keygen' && options.out && positional.length === 0) {
    const { keyPath, publicKey } = generateSigningKey(options.out, options);
    console.log(`Wrote ${keyPath}. Keep it private and out of every repository.`);
    console.log(`publisher.publicKey: ${publicKey}`);
    console.log(`Key fingerprint: ${fingerprint(publicKey)}`);
    return 0;
  }
  if (command === 'sign' && options.key && positional.length === 1) {
    const signed = signPlugin(positional[0], options.key);
    const count = Object.keys(signed.files).length;
    console.log(`Signed ${count} ${count === 1 ? 'file' : 'files'} in ${positional[0]} (key fingerprint ${signed.fingerprint}).`);
    return 0;
  }
  if (command === 'verify' && positional.length === 1 && !options.key && !options.out) {
    const state = verifyPlugin(positional[0]);
    if (state.status === 'Verified') {
      console.log(`Verified, key fingerprint ${state.fingerprint}.`);
      return 0;
    }
    console.error(state.status === 'Unsigned' ? `${positional[0]} has no signature.json (Unsigned).` : `Invalid: ${state.error}`);
    if (state.unlisted.length > 1) console.error(`Not listed in signature.json: ${state.unlisted.join(', ')}`);
    return 1;
  }
  throw new UsageError(command ? `Unexpected arguments for ${command}.` : 'No command given.');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error instanceof UsageError ? `${error.message}\n${usage}` : `sign-plugin: ${error.message}`);
    process.exitCode = error instanceof UsageError ? 2 : 1;
  }
}
