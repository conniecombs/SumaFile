import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const logPath = process.env.SUMAFILE_STARTUP_TIMING_LOG
  ?? path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'SumaFile', 'startup-timing.log');

const budgets = {
  backendReadyMs: 1500,
  workspaceInitializedMs: 6000,
  firstSyncMs: 7000,
  readyMs: 8000,
  driveRefreshMinMs: 4500,
};

if (!fs.existsSync(logPath)) {
  throw new Error(`Startup timing log not found: ${logPath}`);
}

const events = fs.readFileSync(logPath, 'utf8')
  .split(/\r?\n/)
  .map(parseLine)
  .filter(Boolean);

const session = latestSuccessfulSession(events);
if (!session.length) {
  throw new Error(`No successful MainWindow.Connect session found in ${logPath}`);
}

const violations = [];
checkMaximum(session, 'BackendSession.Start.ready', budgets.backendReadyMs, violations);
checkMaximum(session, 'MainWindow.Connect.workspace-initialized', budgets.workspaceInitializedMs, violations);
checkMaximum(session, 'MainWindow.Connect.first-sync', budgets.firstSyncMs, violations);
checkMaximum(session, 'MainWindow.Connect.ready', budgets.readyMs, violations);
checkDeferredDriveRefresh(session, budgets.driveRefreshMinMs, violations);

if (violations.length) {
  console.error(`WinUI startup budget failed for ${logPath}`);
  for (const violation of violations) {
    console.error(`- ${violation}`);
  }

  process.exit(1);
}

const ready = session.findLast(event => event.stage === 'MainWindow.Connect.ready');
console.log(`WinUI startup budget passed: ready in ${ready.totalMs.toFixed(1)} ms (${ready.timestamp}).`);

function parseLine(line) {
  const match = /^\[(?<timestamp>[^\]]+)\]\s+(?<stage>\S+)\s+total_ms=(?<total>[0-9.]+)\s+delta_ms=(?<delta>[0-9.]+)(?:\s+detail=(?<detail>.*))?$/.exec(line);
  if (!match?.groups) {
    return null;
  }

  const totalMs = Number(match.groups.total);
  const deltaMs = Number(match.groups.delta);
  if (!Number.isFinite(totalMs) || !Number.isFinite(deltaMs)) {
    return null;
  }

  return {
    timestamp: match.groups.timestamp,
    stage: match.groups.stage,
    totalMs,
    deltaMs,
    detail: match.groups.detail?.trim() ?? '',
  };
}

function latestSuccessfulSession(allEvents) {
  const beginIndexes = allEvents
    .map((event, index) => [event, index])
    .filter(([event]) => event.stage === 'MainWindow.Connect.begin')
    .map(([, index]) => index);

  for (let i = beginIndexes.length - 1; i >= 0; i -= 1) {
    const begin = beginIndexes[i];
    const nextBegin = beginIndexes[i + 1] ?? allEvents.length;
    const session = allEvents.slice(begin, nextBegin);
    if (session.some(event => event.stage === 'MainWindow.Connect.ready')) {
      return session;
    }
  }

  return [];
}

function checkMaximum(session, stage, maxMs, violations) {
  const event = session.findLast(item => item.stage === stage);
  if (!event) {
    violations.push(`Missing startup marker: ${stage}`);
    return;
  }

  if (event.totalMs > maxMs) {
    violations.push(`${stage} took ${event.totalMs.toFixed(1)} ms; budget is ${maxMs} ms`);
  }
}

function checkDeferredDriveRefresh(session, minMs, violations) {
  for (const event of session) {
    if (!event.stage.startsWith('MainWindow.StartupDriveRefresh.')) {
      continue;
    }

    if (!['refreshed', 'skipped', 'skipped-busy'].some(marker => event.stage.endsWith(`.${marker}`))) {
      continue;
    }

    if (event.totalMs < minMs) {
      violations.push(`${event.stage} ran at ${event.totalMs.toFixed(1)} ms; minimum delay is ${minMs} ms`);
    }
  }
}
