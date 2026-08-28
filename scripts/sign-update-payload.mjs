import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

function fail(message) {
  console.error(`Updater signing failed: ${message}`);
  process.exit(1);
}

function parseArgs() {
  return Object.fromEntries(
    process.argv
      .slice(2)
      .filter((arg) => arg.startsWith('--') && arg.includes('='))
      .map((arg) => {
        const trimmed = arg.slice(2);
        const split = trimmed.indexOf('=');
        return [trimmed.slice(0, split), trimmed.slice(split + 1)];
      }),
  );
}

function normalizeSecretKey(value) {
  const trimmed = value.trim().replace(/\\n/g, '\n');
  if (trimmed.includes('BEGIN')) {
    return trimmed;
  }

  try {
    const decoded = Buffer.from(trimmed, 'base64').toString('utf8').replace(/\\n/g, '\n');
    if (decoded.includes('BEGIN')) {
      return decoded;
    }
  } catch {
  }

  return trimmed;
}

function publicKeyBase64(privateKey) {
  const publicKey = crypto.createPublicKey(privateKey);
  const jwk = publicKey.export({ format: 'jwk' });
  if (jwk.kty !== 'OKP' || jwk.crv !== 'Ed25519' || !jwk.x) {
    fail('SIMPLEFILE_SIGNING_PRIVATE_KEY must be an Ed25519 private key.');
  }

  return Buffer.from(jwk.x, 'base64url').toString('base64');
}

const args = parseArgs();
const file = args.file ? path.resolve(args.file) : '';
const out = args.out ? path.resolve(args.out) : '';
const requirePublicKey = args['require-public-key'] === 'true';
const privateKeyContent = process.env.SIMPLEFILE_SIGNING_PRIVATE_KEY;

if (!file) {
  fail('Pass --file=path-to-installer.');
}
if (!out) {
  fail('Pass --out=path-to-signature.');
}
if (!fs.existsSync(file)) {
  fail(`Payload does not exist: ${file}`);
}
if (!privateKeyContent) {
  fail('SIMPLEFILE_SIGNING_PRIVATE_KEY is required.');
}

const privateKey = crypto.createPrivateKey({
  key: normalizeSecretKey(privateKeyContent),
  passphrase: process.env.SIMPLEFILE_SIGNING_PRIVATE_KEY_PASSWORD || undefined,
});
if (privateKey.asymmetricKeyType !== 'ed25519') {
  fail(`SIMPLEFILE_SIGNING_PRIVATE_KEY must be Ed25519, got ${privateKey.asymmetricKeyType}.`);
}

const derivedPublicKey = publicKeyBase64(privateKey);
const configuredPublicKey = process.env.SIMPLEFILE_UPDATER_PUBLIC_KEY?.trim();
if (requirePublicKey && !configuredPublicKey) {
  fail('SIMPLEFILE_UPDATER_PUBLIC_KEY is required when updater signatures are required.');
}
if (configuredPublicKey && configuredPublicKey !== derivedPublicKey) {
  fail('SIMPLEFILE_UPDATER_PUBLIC_KEY does not match SIMPLEFILE_SIGNING_PRIVATE_KEY.');
}

const payload = fs.readFileSync(file);
const signature = crypto.sign(null, payload, privateKey).toString('base64');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, `${signature}\n`, 'utf8');
console.log(JSON.stringify({ signatureFile: out, publicKey: derivedPublicKey }));
