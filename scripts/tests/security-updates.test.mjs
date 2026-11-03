import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { auditTargets, selectPatch, lockPackages, validatePatchGraph, updateSecurity } from '../lib/security-updates.mjs';
import { classifyFailure, validateRepairFiles } from '../lib/maintenance-policy.mjs';
const source = 'registry+https://github.com/rust-lang/crates.io-index';
const pkg = { name: 'sample', version: '0.7.5', source };
const lock = v => `version = 4\n\n[[package]]\nname = "sample"\nversion = "${v}"\nsource = "${source}"\n`;
const report = { vulnerabilities: { list: [{ package: pkg }] }, warnings: { yanked: [{ package: pkg }] } };

test('select only stable non-yanked newer patches', () => {
  assert.equal(selectPatch('0.7.5', [{ num: '0.7.6', yanked: true }, { num: '0.7.8' }, { num: '0.8.0' }, { num: '0.7.9-rc.1' }]), '0.7.8');
  assert.throws(() => selectPatch('0.7.5', [{ num: '0.8.0' }, { num: '0.7.4' }]), /No stable patch/);
  assert.equal(auditTargets(report).length, 1);
  assert.throws(() => auditTargets({}), /Invalid/);
  assert.throws(() => auditTargets({ ...report, warnings: { unmaintained: [{}] } }), /review/);
  assert.throws(() => auditTargets({ ...report, vulnerabilities: { list: [{ package: { ...pkg, name: '--help' , source: 'git+evil' } }] } }), /crates.io/);
});
test('patch graph rejects broader updates, sources, additions and removals', () => {
  const before = lockPackages(lock('0.7.5'));
  validatePatchGraph(before, lockPackages(lock('0.7.8')));
  for (const after of [lockPackages(lock('0.8.0')), [], [...before, pkg], [{ ...pkg, source: 'git+evil' }], [{ ...pkg, version: '0.7.4' }]]) {
    assert.throws(() => validatePatchGraph(before, after), /review/);
  }
});
for (const failure of [null, 'update', 'audit', 'network', 'graph']) test(`security transaction rolls back on ${failure ?? 'success retains patch'}`, async () => {
  const cwd = mkdtempSync(join(tmpdir(), 'sumafile-security-'));
  const path = join(cwd, 'Cargo.lock'); writeFileSync(path, lock('0.7.5'));
  const calls = [];
  const run = args => {
    calls.push(args);
    if (args.includes('--json')) return { status: 1, stdout: JSON.stringify(report) };
    if (args[0] === 'update') {
      writeFileSync(path, lock(failure === 'graph' ? '0.8.0' : '0.7.8'));
      return { status: failure === 'update' ? 1 : 0, stderr: 'fixture update failure' };
    }
    return { status: failure === 'audit' ? 1 : 0, stderr: 'fixture audit failure' };
  };
  try {
    const task = updateSecurity({ cwd, run, versions: async () => {
      if (failure === 'network') throw new Error('network down');
      return [{ num: '0.7.8', yanked: false }];
    } });
    if (failure) { await assert.rejects(task); assert.equal(readFileSync(path, 'utf8'), lock('0.7.5')); }
    else {
      await task;
      assert.equal(readFileSync(path, 'utf8'), lock('0.7.8'));
      assert.deepEqual(calls[1], ['update', '--package', 'sample@0.7.5', '--precise', '0.7.8']);
      assert.ok(calls[2].includes('--deny'));
    }
  } finally { rmSync(cwd, { recursive: true, force: true }); }
});
test('clean audit produces no updates; operational failures do not masquerade as clean', async () => {
  const cwd = mkdtempSync(join(tmpdir(), 'sumafile-security-'));
  writeFileSync(join(cwd, 'Cargo.lock'), lock('0.7.5'));
  try {
    assert.deepEqual(await updateSecurity({ cwd, run: () => ({ status: 0, stdout: JSON.stringify({ vulnerabilities: { list: [] }, warnings: {} }) }) }), []);
    await assert.rejects(updateSecurity({ cwd, run: () => ({ status: 2, stderr: 'database unavailable' }) }), /Audit failed/);
  } finally { rmSync(cwd, { recursive: true, force: true }); }
});
test('audit-only PR failures re-audit main; mixed failures, forks and stale heads require review', () => {
  const input = { repository: 'owner/repo', mainSha: 'a'.repeat(40), branchHead: 'b'.repeat(40), log: 'RUSTSEC-2026-0387',
    run: { name: 'CI', path: '.github/workflows/ci.yml', status: 'completed', conclusion: 'failure', head_branch: 'dependabot/test', head_sha: 'b'.repeat(40), head_repository: { full_name: 'owner/repo' } },
    jobs: [{ name: 'Security checks', conclusion: 'failure', steps: [{ name: 'Audit Rust dependencies', conclusion: 'failure' }] }] };
  assert.equal(classifyFailure(input).recipe, 'security');
  assert.equal(classifyFailure({ ...input, branchHead: 'c'.repeat(40) }).action, 'review');
  assert.equal(classifyFailure({ ...input, run: { ...input.run, head_repository: { full_name: 'fork/repo' } } }).action, 'review');
  assert.equal(classifyFailure({ ...input, jobs: [...input.jobs, { name: 'Rust quality gate', conclusion: 'failure', steps: [{ name: 'Run tests', conclusion: 'failure' }] }] }).action, 'review');
  assert.equal(validateRepairFiles('security', [{ filename: 'Cargo.lock', status: 'modified', additions: 2, deletions: 2 }]), true);
  assert.equal(validateRepairFiles('security', [{ filename: 'scripts/lib/audit-policy.mjs', status: 'modified', additions: 2, deletions: 2 }]), false);
});

test('actionable unsoundness warnings are repaired under the release audit policy', () => {
  assert.deepEqual(auditTargets({ vulnerabilities: { list: [] }, warnings: { unsound: [{ package: pkg, versions: { patched: ['>=0.7.8'] } }] } }), [pkg]);
});
