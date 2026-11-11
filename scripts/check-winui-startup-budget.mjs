import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// --expect-plugins: the session must also have discovered at least one plugin (count>=1). CI passes it right after the
// -PluginsDir smoke, so a session without plugins (another run's) cannot stand in for the one it means to budget.
const args = process.argv.slice(2);
for (const arg of args) {
  if (arg !== '--expect-plugins') {
    console.error(`Unknown argument ${arg}. Usage: node scripts/check-winui-startup-budget.mjs [--expect-plugins]`);
    process.exit(2);
  }
}
const expectPlugins = args.includes('--expect-plugins');

const logPath = process.env.SUMAFILE_STARTUP_TIMING_LOG
  ?? path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'SumaFile', 'startup-timing.log');

const budgets = {
  backendReadyMs: 1500,
  workspaceInitializedMs: 6000,
  firstSyncMs: 7000,
  readyMs: 8000,
  driveRefreshMinMs: 4500,
  // Spec section 5.2, over the whole plugin discovery span (pluginDiscoverySpan), however the App splits it.
  pluginsDiscoveredSpanMs: 50,
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

// Printed whether or not the budget holds, stage by stage, so a CI log shows which part of discovery dominates.
// Required: the App discovers plugins on every start, -NoPlugins included (MainWindow.ConnectAsync runs
// InitializePluginsAsync before the workspace loads, and marks plugins-discovered with count=0 when there are none), so
// a session that reached ready without the marker never measured the span and must not pass as if it had.
const pluginSpan = pluginDiscoverySpan(session);
if (!pluginSpan) {
  violations.push(`Missing startup marker: MainWindow.Connect.plugins-discovered; the 50 ms plugin discovery budget could not be checked`);
} else {
  const stages = pluginSpan.stages.map(stage => `${stage.marker} ${stage.deltaMs.toFixed(1)} ms`).join(', ');
  console.log(`plugins-discovered span ${pluginSpan.totalMs.toFixed(1)} ms (budget ${budgets.pluginsDiscoveredSpanMs} ms, ${pluginSpan.detail || 'no detail'}): ${stages}.`);
  if (pluginSpan.totalMs > budgets.pluginsDiscoveredSpanMs) {
    const count = pluginSpan.stages.length === 1 ? '1 stage' : `${pluginSpan.stages.length} stages`;
    violations.push(`MainWindow.Connect.plugins-discovered took ${pluginSpan.totalMs.toFixed(1)} ms over ${count}; budget is ${budgets.pluginsDiscoveredSpanMs} ms`);
  }
  const discovered = Number(/(?:^|\s)count=(\d+)(?:\s|$)/.exec(pluginSpan.detail)?.[1] ?? NaN);
  if (expectPlugins && !(discovered >= 1)) {
    violations.push(`The latest session discovered no plugins (${pluginSpan.detail || 'no count'}), but --expect-plugins needs a session that ran with plugins`);
  }
}

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

// The plugin discovery span: MainWindow.Connect.plugins-discovered and the MainWindow.Connect.plugins-* sub-stages
// written just before it. Each delta runs from the previous MainWindow.Connect stage, so the span runs from the stage
// before the first of them (backend-started) to plugins-discovered, and a log from before the split budgets
// plugins-discovered alone. Lines of other scopes are skipped. Null without a plugins-discovered stage, which the
// caller reports as a violation.
function pluginDiscoverySpan(sessionEvents) {
  const scope = 'MainWindow.Connect.';
  const end = sessionEvents.findLastIndex(event => event.stage === `${scope}plugins-discovered`);
  if (end < 0) {
    return null;
  }

  const stages = [];
  for (let index = end; index >= 0; index -= 1) {
    const event = sessionEvents[index];
    if (!event.stage.startsWith(scope)) {
      continue;
    }

    const marker = event.stage.slice(scope.length);
    if (!marker.startsWith('plugins-')) {
      break;
    }

    stages.unshift({ marker, deltaMs: event.deltaMs });
  }

  return {
    totalMs: stages.reduce((sum, stage) => sum + stage.deltaMs, 0),
    stages,
    detail: sessionEvents[end].detail,
  };
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
