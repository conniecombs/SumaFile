import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parseWorkflow, validateWorkflows } from '../lib/workflow-policy.mjs';

// Plan phase 5, Task 2: the SDK packs as SumaFile.PluginSdk into target/packages, as a dry run in the WinUI host job
// after the tests. Nothing is published. PluginSdkPackageTests (C#) checks the project metadata in detail.
const root = fileURLToPath(new URL('../..', import.meta.url));
const read = relative => readFileSync(path.join(root, relative), 'utf8');

function workflows() {
  return {
    'ci.yml': parseWorkflow(read('.github/workflows/ci.yml')),
    'release.yml': parseWorkflow(read('.github/workflows/release.yml')),
    'release-build.yml': parseWorkflow(read('.github/workflows/release-build.yml')),
    'installer-smoke.yml': parseWorkflow(read('.github/workflows/installer-smoke.yml')),
  };
}
const dependabot = () => parseWorkflow(read('.github/dependabot.yml'));
const winuiSteps = all => all['ci.yml'].jobs.winui.steps;
const isPack = step => /^npm run pack:sdk\s*$/m.test(step.run ?? '');

test('pack:sdk packs the SDK into target/packages and publishes nothing', () => {
  const scripts = JSON.parse(read('package.json')).scripts;
  assert.equal(scripts['pack:sdk'], 'dotnet pack src-winui/SimpleFile.PluginSdk -c Release -o target/packages');
  for (const command of Object.values(scripts)) assert.doesNotMatch(command, /nuget push/i);
  assert.ok(existsSync(path.join(root, 'src-winui/SimpleFile.PluginSdk/README.md')), 'the package README exists');
});

test('the WinUI host job packs the SDK once, after the WinUI tests', () => {
  const steps = winuiSteps(workflows());
  const packs = steps.filter(isPack);
  assert.equal(packs.length, 1);
  assert.equal(packs[0].name, 'Pack the plugin SDK (dry run)');
  const tests = steps.findIndex(step => /^npm run check:winui\s*$/m.test(step.run ?? ''));
  assert.ok(tests >= 0 && steps.indexOf(packs[0]) > tests, 'the pack step follows "npm run check:winui"');
});

test('the workflow policy requires the SDK pack step and its place', () => {
  const missing = workflows();
  missing['ci.yml'].jobs.winui.steps = winuiSteps(missing).filter(step => !isPack(step));
  assert.ok(validateWorkflows(missing, dependabot()).some(error => error.includes('plugin SDK pack')));

  const early = workflows();
  const steps = winuiSteps(early);
  const pack = steps.find(isPack);
  early['ci.yml'].jobs.winui.steps = [pack, ...steps.filter(step => step !== pack)];
  assert.ok(validateWorkflows(early, dependabot()).some(error => error.includes('after the WinUI tests')));
});

test('the packaging check accepts the SDK package wiring', () => {
  const result = spawnSync(process.execPath, [path.join(root, 'scripts/check-winui-packaging.mjs')], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.doesNotMatch(result.stderr, /PluginSdk|pack:sdk/);
});
