import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { auditArgs } from './audit-policy.mjs';

const version = value => /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value) ? value.split('.').map(Number) : null;
const registry = 'registry+https://github.com/rust-lang/crates.io-index';

// Only read Cargo's generated package headers. Cargo remains the lockfile writer.
export function lockPackages(text) {
  const blocks = text.split(/^\[\[package\]\]\s*$/m).slice(1);
  if (!blocks.length) throw new Error('No Cargo packages found');
  return blocks.map(block => {
    const field = key => new RegExp(`^${key} = "([^"\\r\\n]+)"$`, 'm').exec(block)?.[1];
    const item = { name: field('name'), version: field('version'), source: field('source') ?? '' };
    if (!item.name || !item.version) throw new Error('Invalid Cargo package header');
    return item;
  });
}

export function validatePatchGraph(before, after) {
  if (before.length !== after.length) throw new Error('Package additions/removals require review');
  const remaining = [...after];
  for (const old of before) {
    // Match unchanged versions first, including multiple installed versions.
    let index = remaining.findIndex(p => p.name === old.name && p.source === old.source && p.version === old.version);
    if (index < 0) {
      const a = version(old.version);
      index = remaining.findIndex(p => {
        const b = version(p.version);
        return old.source === registry && p.source === registry && p.name === old.name && a && b
          && a[0] === b[0] && a[1] === b[1] && b[2] > a[2];
      });
    }
    if (index < 0) throw new Error(`Non-patch or source change requires review: ${old.name} ${old.version}`);
    remaining.splice(index, 1);
  }
}

export function auditTargets(report) {
  if (!Array.isArray(report?.vulnerabilities?.list) || typeof report?.warnings !== 'object' || report.warnings === null) {
    throw new Error('Invalid cargo-audit report');
  }
  const warnings = [];
  for (const [kind, entries] of Object.entries(report.warnings)) {
    if (!Array.isArray(entries)) throw new Error('Invalid audit warning list');
    for (const entry of entries) {
      if (kind !== 'yanked' && !entry.versions?.patched?.length) throw new Error('Audit warning has no patch; review required');
      warnings.push(entry);
    }
  }
  const entries = [...report.vulnerabilities.list, ...warnings];
  const targets = new Map();
  for (const { package: pkg } of entries) {
    if (!pkg || !/^[A-Za-z0-9_-]+$/.test(pkg.name) || !version(pkg.version) || pkg.source !== registry) {
      throw new Error('Only stable crates.io packages can be repaired automatically');
    }
    targets.set(`${pkg.name}@${pkg.version}`, pkg);
  }
  return [...targets.values()];
}

export function selectPatch(current, releases) {
  const a = version(current);
  const candidates = releases.filter(r => !r.yanked && version(r.num)).map(r => r.num)
    .filter(v => { const b = version(v); return a && b[0] === a[0] && b[1] === a[1] && b[2] > a[2]; });
  candidates.sort((a, b) => version(b)[2] - version(a)[2]);
  if (!candidates.length) throw new Error(`No stable patch for ${current}; review required`);
  return candidates[0];
}

async function registryVersions(name) {
  const response = await fetch(`https://crates.io/api/v1/crates/${encodeURIComponent(name)}`, {
    signal: AbortSignal.timeout(30_000), headers: { 'User-Agent': 'SumaFile-security-maintenance' },
  });
  if (!response.ok) throw new Error(`crates.io lookup failed: HTTP ${response.status}`);
  const data = await response.json();
  if (!Array.isArray(data.versions)) throw new Error('Invalid crates.io versions response');
  return data.versions;
}

function cargo(args, cwd) {
  const env = { ...process.env };
  delete env.GH_TOKEN;
  delete env.GITHUB_TOKEN;
  const result = spawnSync('cargo', args, { cwd, env, encoding: 'utf8', maxBuffer: 20 * 1024 * 1024, timeout: 300_000 });
  if (result.error) throw result.error;
  return result;
}

export async function updateSecurity({ cwd = process.cwd(), run = cargo, versions = registryVersions } = {}) {
  const path = resolve(cwd, 'Cargo.lock');
  const original = readFileSync(path, 'utf8');
  try {
    const audit = run([...auditArgs, '--json'], cwd);
    if (![0, 1].includes(audit.status)) throw new Error(`Audit failed: ${audit.stderr}`);
    const targets = auditTargets(JSON.parse(audit.stdout));
    if (!targets.length) {
      if (audit.status !== 0) throw new Error('Audit failed without repairable findings');
      return [];
    }
    if (targets.length > 20) throw new Error('Too many affected packages; review required');
    const before = lockPackages(original);
    const updates = [];
    for (const pkg of targets) {
      if (!before.some(p => p.name === pkg.name && p.version === pkg.version && p.source === pkg.source)) {
        throw new Error('Audit finding does not match the trusted lockfile');
      }
      const next = selectPatch(pkg.version, await versions(pkg.name));
      // A previous targeted update may have already patched this dependency.
      if (lockPackages(readFileSync(path, 'utf8')).some(p => p.name === pkg.name && p.version === pkg.version)) {
        const result = run(['update', '--package', `${pkg.name}@${pkg.version}`, '--precise', next], cwd);
        if (result.status !== 0) throw new Error(`Targeted update failed: ${result.stderr}`);
      }
      updates.push(`${pkg.name}: ${pkg.version} -> ${next}`);
    }
    validatePatchGraph(before, lockPackages(readFileSync(path, 'utf8')));
    const final = run(auditArgs, cwd);
    if (final.status !== 0) throw new Error(`Patched lockfile still fails the release audit: ${final.stderr}`);
    return updates;
  } catch (error) {
    writeFileSync(path, original);
    throw error;
  }
}
