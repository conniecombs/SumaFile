import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parseWorkflow } from '../lib/workflow-policy.mjs';

// Plan phase 5, Task 7: a passing UI smoke deletes its test-results\ui-smoke\<run> folder (the 100,000-file fixtures
// otherwise pile up), keeps it on failure or with -KeepResults, and leaves last-passed-report.json beside it.
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const script = readFileSync(path.join(repoRoot, 'scripts', 'smoke-winui-ui.ps1'), 'utf8');

// The text of `function <name> { ... }`, by brace matching (the functions hold no braces inside strings).
function functionBody(source, name) {
  const start = source.indexOf(`function ${name} {`);
  assert.ok(start >= 0, `smoke-winui-ui.ps1 defines ${name}`);
  let depth = 0;
  for (let index = source.indexOf('{', start); index < source.length; index += 1) {
    if (source[index] === '{') depth += 1;
    if (source[index] === '}' && --depth === 0) return source.slice(start, index + 1);
  }
  throw new Error(`${name} is not closed`);
}

const indexOf = (text, needle) => {
  const index = text.indexOf(needle);
  assert.ok(index >= 0, `smoke-winui-ui.ps1 contains ${needle}`);
  return index;
};
const lastIndexOf = (text, needle) => {
  const index = text.lastIndexOf(needle);
  assert.ok(index >= 0, `smoke-winui-ui.ps1 contains ${needle}`);
  return index;
};

test('-KeepResults is a switch and the run folder lives under test-results\\ui-smoke with its marker', () => {
  const param = script.slice(0, indexOf(script, "$ErrorActionPreference = 'Stop'"));
  assert.match(param, /\[switch\]\$KeepResults\b/);
  const results = indexOf(script, "$uiSmokeRoot = Join-Path $root 'test-results\\ui-smoke'");
  const run = indexOf(script, '$testRoot = Join-Path $uiSmokeRoot $runName');
  const created = indexOf(script, 'New-Item -ItemType Directory -Path $testRoot');
  const marker = indexOf(script, "[System.IO.File]::WriteAllText((Join-Path $testRoot '.sumafile-ui-smoke')");
  assert.ok(results < run && run < created && created < marker, 'the marker is written right after the folder is made');
  assert.ok(marker < indexOf(script, '$process = [System.Diagnostics.Process]::Start($start)'), 'before the app starts');
});

test('the delete is guarded: directly under test-results\\ui-smoke, run name, marker, no reparse point', () => {
  const body = functionBody(script, 'Remove-UiSmokeRunFolder');
  const remove = indexOf(body, '[System.IO.Directory]::Delete($run, $true)');
  for (const guard of [
    '[System.IO.Path]::GetDirectoryName($run)',
    '$ResultsRoot',
    '-notmatch $UiSmokeRunNamePattern',
    "'.sumafile-ui-smoke'",
    'ReparsePoint',
  ]) {
    assert.ok(indexOf(body, guard) < remove, `${guard} is checked before the folder is deleted`);
  }
  // The only recursive delete in the script, and nothing else removes the run folder.
  assert.equal(script.split('[System.IO.Directory]::Delete(').length - 1, 1);
  assert.doesNotMatch(script, /Remove-Item\b[^\n]*\$testRoot/i);
  assert.doesNotMatch(script, /\brd\s+\/s|\brmdir\b/i);
});

test('the delete runs only on the success path, after the app and every helper process are gone', () => {
  const tryStart = indexOf(script, '$process = [System.Diagnostics.Process]::Start($start)');
  const appGone = indexOf(script, '$process.Dispose()');
  const serverGone = indexOf(script, '$remoteServer.Dispose()');
  const afterRunChecks = indexOf(script, "if ($smokeCompleted) { throw (($afterRunFailures -join");
  const call = script.indexOf('Remove-UiSmokeRunFolder -RunRoot');
  assert.ok(call > 0, 'the script calls Remove-UiSmokeRunFolder');
  assert.equal(script.split('Remove-UiSmokeRunFolder -RunRoot').length - 1, 1, 'from one place only');
  const tail = script.slice(afterRunChecks);
  const finallyEnd = afterRunChecks + tail.search(/\n\}\s*\n/);
  assert.ok(finallyEnd > afterRunChecks, 'the try/finally ends at the top level');
  assert.ok(tryStart < appGone && appGone < serverGone && serverGone < afterRunChecks && afterRunChecks < finallyEnd);
  // After the try/finally at the top level: any throw above (a failed scenario, a timeout, an after-run check)
  // ends the script before this point, so a failed run keeps its folder.
  assert.ok(call > finallyEnd, 'the delete runs after the try/finally, never inside it');
  const success = script.slice(finallyEnd);
  const completed = indexOf(success, 'if (-not $smokeCompleted)');
  const copy = indexOf(success, "Copy-Item -LiteralPath $reportPath -Destination $passedReportPath");
  const printed = indexOf(success, 'Write-Host "UI runtime smoke passed. Report: $passedReportPath"');
  const keep = indexOf(success, 'if ($KeepResults)');
  const wait = indexOf(success, 'Wait-UiSmokeHelperProcessesGone');
  const remove = success.indexOf('Remove-UiSmokeRunFolder -RunRoot');
  assert.ok(completed < copy && copy < printed && printed < keep && keep < wait && wait < remove,
    'report copied and printed, then -KeepResults, then the helpers are awaited, then the delete');
  assert.match(script, /\$passedReportPath = Join-Path \$uiSmokeRoot 'last-passed-report\.json'/);
  // The old success line, which printed the run folder's report, is gone.
  assert.doesNotMatch(script, /smoke passed\. Report: \$reportPath/);
});

test('the helper wait covers the app\'s process tree, its executables and the run folder', () => {
  const body = functionBody(script, 'Get-UiSmokeHelperProcesses') + functionBody(script, 'Get-UiSmokeProcessTable');
  assert.match(body, /Get-UiSmokeProcessTable \|/, 'the helper list reads the process table');
  // Not Get-CimInstance: CimCmdlets can be missing when Windows PowerShell inherits a pwsh PSModulePath.
  assert.doesNotMatch(script, /Get-CimInstance|Get-WmiObject/);
  for (const needle of ['Win32_Process', 'ParentProcessId', 'ExecutablePath', 'CommandLine', 'CreationDate']) {
    assert.ok(body.includes(needle), `Get-UiSmokeHelperProcesses reads ${needle}`);
  }
  const wait = functionBody(script, 'Wait-UiSmokeHelperProcessesGone');
  assert.match(wait, /Get-UiSmokeHelperProcesses/);
  // The app and the SFTP server ids are recorded at launch, while their objects still exist.
  assert.ok(indexOf(script, '$appProcessId = $process.Id') < indexOf(script, '$process.Dispose()'));
  assert.ok(indexOf(script, '$serverProcessId = $remoteServer.Id') < indexOf(script, '$remoteServer.Dispose()'));
});

test('CI uploads the passed report and the startup budget still reads its log', () => {
  const winui = parseWorkflow(readFileSync(path.join(repoRoot, '.github', 'workflows', 'ci.yml'), 'utf8')).jobs.winui;
  const upload = winui.steps.find(step => step.name === 'Upload WinUI runtime reports');
  assert.ok(upload, 'ci.yml winui uploads the runtime reports');
  const paths = upload.with.path.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  assert.ok(paths.includes('test-results/ui-smoke/last-passed-report.json'), 'the last passed report is uploaded');
  assert.ok(paths.includes('test-results/ui-smoke/plugins-passed-report.json'), 'the plugin run report is uploaded');
  assert.ok(paths.includes('test-results/ui-smoke/**/report.json'), 'a failed run keeps its folder and report');
  // The two smokes share last-passed-report.json; the plugin run's copy is kept before the plain run replaces it.
  const names = winui.steps.map(step => step.name);
  const pluginSmoke = names.indexOf('Smoke WinUI rendering, layout, cancellation, and the Hello plugin');
  const keep = names.indexOf('Keep the plugin smoke report');
  const plainSmoke = names.indexOf('Smoke WinUI rendering, layout, and cancellation without plugins');
  assert.ok(pluginSmoke >= 0 && pluginSmoke < keep && keep < plainSmoke);
  assert.match(winui.steps[keep].run, /last-passed-report\.json.*plugins-passed-report\.json/);
  // The startup budget reads %LOCALAPPDATA%\SumaFile\startup-timing.log, never the deleted run folder.
  assert.doesNotMatch(readFileSync(path.join(repoRoot, 'scripts', 'check-winui-startup-budget.mjs'), 'utf8'), /ui-smoke/);
});

const pwsh = ['pwsh', 'powershell'].find(name => spawnSync(name, ['-NoProfile', '-Command', 'exit 0']).status === 0);

test('Remove-UiSmokeRunFolder deletes only a marked run folder directly under the results root', { skip: !pwsh && 'PowerShell is not installed' }, () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'sumafile-ui-smoke-cleanup-'));
  try {
    const results = path.join(dir, 'test-results', 'ui-smoke');
    const run = name => {
      const folder = path.join(results, name);
      mkdirSync(path.join(folder, 'state', 'nested'), { recursive: true });
      writeFileSync(path.join(folder, 'state', 'nested', 'file.txt'), 'data');
      return folder;
    };
    const passed = run('20261110-120000-0123abcd');
    writeFileSync(path.join(passed, '.sumafile-ui-smoke'), 'disposable UI test data');
    const unmarked = run('20261110-120001-0123abcd');
    const misnamed = run('not-a-run');
    writeFileSync(path.join(misnamed, '.sumafile-ui-smoke'), 'disposable UI test data');
    const outside = path.join(dir, 'elsewhere', '20261110-120002-0123abcd');
    mkdirSync(outside, { recursive: true });
    writeFileSync(path.join(outside, '.sumafile-ui-smoke'), 'disposable UI test data');

    const harness = path.join(dir, 'harness.ps1');
    const pattern = /^\$UiSmokeRunNamePattern = .+$/m.exec(script)?.[0];
    assert.ok(pattern, 'the run name pattern is a top-level variable');
    writeFileSync(harness, [
      "$ErrorActionPreference = 'Stop'",
      'Set-StrictMode -Version 3.0',
      pattern,
      functionBody(script, 'Remove-UiSmokeRunFolder'),
      ...[passed, unmarked, misnamed, outside].map(folder =>
        `Write-Output ("{0}={1}" -f '${path.basename(path.dirname(folder))}/${path.basename(folder)}', (Remove-UiSmokeRunFolder -RunRoot '${folder}' -ResultsRoot '${results}'))`),
    ].join('\n'));
    const result = spawnSync(pwsh, ['-NoProfile', '-NonInteractive', '-File', harness], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /ui-smoke\/20261110-120000-0123abcd=True/);
    assert.match(result.stdout, /ui-smoke\/20261110-120001-0123abcd=False/);
    assert.match(result.stdout, /ui-smoke\/not-a-run=False/);
    assert.match(result.stdout, /elsewhere\/20261110-120002-0123abcd=False/);
    assert.equal(existsSync(passed), false, 'the marked run folder is deleted');
    for (const kept of [unmarked, misnamed, outside]) {
      assert.ok(existsSync(path.join(kept, 'state', 'nested', 'file.txt')) || existsSync(kept), `${kept} is kept`);
    }
    assert.ok(existsSync(results), 'the results root itself is kept');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
