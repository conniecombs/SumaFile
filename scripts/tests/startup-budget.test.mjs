import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('../check-winui-startup-budget.mjs', import.meta.url));

const line = (stage, total, delta, detail) =>
  `[2026-11-09T10:00:00.0000000-06:00] ${stage} total_ms=${total.toFixed(1)} delta_ms=${delta.toFixed(1)}${detail ? ` detail=${detail}` : ''}`;

// One App session around the given plugins-* lines, with every other budget met.
const session = pluginLines => [
  line('MainWindow.Connect.begin', 0, 0),
  line('BackendSession.Start.ready', 230, 4),
  line('MainWindow.Connect.backend-started', 240, 240),
  ...pluginLines,
  line('MainWindow.Connect.workspace-initialized', 900, 600),
  line('MainWindow.Connect.first-sync', 1100, 200),
  line('MainWindow.Connect.ready', 1150, 50),
  line('MainWindow.StartupDriveRefresh.queued', 0, 0),
  line('MainWindow.StartupDriveRefresh.refreshed', 5050, 5050),
];

function check(lines) {
  const dir = mkdtempSync(path.join(tmpdir(), 'sumafile-startup-budget-'));
  try {
    const log = path.join(dir, 'startup-timing.log');
    writeFileSync(log, `${lines.join('\r\n')}\r\n`);
    return spawnSync(process.execPath, [script], {
      env: { ...process.env, SUMAFILE_STARTUP_TIMING_LOG: log },
      encoding: 'utf8',
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('the plugin span adds up its sub-stages and fails over budget although no stage alone is', () => {
  const result = check(session([
    line('MainWindow.Connect.plugins-begin', 241, 1),
    line('MainWindow.Connect.plugins-manager-built', 251, 10),
    line('MainWindow.Connect.plugins-scan-awaited', 261, 10),
    // Another timer's line inside the span is not part of it.
    line('ExplorerWorkspace.Initialize.begin', 0, 0),
    line('MainWindow.Connect.plugins-evaluated', 271, 10),
    line('MainWindow.Connect.plugins-discovered', 301, 30, 'count=3'),
  ]));

  assert.equal(result.status, 1, result.stdout + result.stderr);
  assert.match(result.stdout, /plugins-discovered span 61\.0 ms \(budget 50 ms, count=3\): plugins-begin 1\.0 ms, plugins-manager-built 10\.0 ms, plugins-scan-awaited 10\.0 ms, plugins-evaluated 10\.0 ms, plugins-discovered 30\.0 ms\./);
  assert.match(result.stderr, /MainWindow\.Connect\.plugins-discovered took 61\.0 ms over 5 stages; budget is 50 ms/);
});

test('a plugin span under budget passes and still prints every sub-stage', () => {
  const result = check(session([
    line('MainWindow.Connect.plugins-begin', 240.5, 0.5),
    line('MainWindow.Connect.plugins-manager-built', 243.5, 3),
    line('MainWindow.Connect.plugins-scan-awaited', 243.5, 0),
    line('MainWindow.Connect.plugins-evaluated', 245, 1.5),
    line('MainWindow.Connect.plugins-discovered', 254, 9, 'count=3'),
  ]));

  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /plugins-discovered span 14\.0 ms \(budget 50 ms, count=3\): plugins-begin 0\.5 ms, plugins-manager-built 3\.0 ms, plugins-scan-awaited 0\.0 ms, plugins-evaluated 1\.5 ms, plugins-discovered 9\.0 ms\./);
  assert.match(result.stdout, /WinUI startup budget passed/);
});

test('a session logged before the sub-stages budgets the plugins-discovered delta alone', () => {
  // The shape CI logged at 46860dc: plugins-discovered directly after backend-started.
  const result = check(session([line('MainWindow.Connect.plugins-discovered', 314.2, 74.2, 'count=3')]));

  assert.equal(result.status, 1, result.stdout + result.stderr);
  assert.match(result.stdout, /plugins-discovered span 74\.2 ms \(budget 50 ms, count=3\): plugins-discovered 74\.2 ms\./);
  assert.match(result.stderr, /MainWindow\.Connect\.plugins-discovered took 74\.2 ms over 1 stage; budget is 50 ms/);
});

test('a session without plugin discovery is not budgeted for it', () => {
  const result = check(session([]));

  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.doesNotMatch(result.stdout, /plugins-discovered/);
});
