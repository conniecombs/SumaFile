import { maintenanceMetadata } from './maintenance-policy.mjs';

// A maintenance proposal is a single commit on the main it was computed from, and the merge gate insists on current
// main, so a proposal main has moved past can never merge; a lock-file repair is pointless once the Dependabot pull
// request it took its versions from is closed or has moved. Such proposals used to stay open and red until someone
// closed them by hand (#91). The gate sweeps them on every run.

const short = sha => sha.slice(0, 12);

/** Why `metadata`'s proposal can no longer merge, or null while it still can. */
export function staleMaintenanceReason({ metadata, mainSha, dependabotPull }) {
  if (metadata.source !== mainSha) {
    return `main moved from ${short(metadata.source)} to ${short(mainSha)}; a repair is a single commit on current main, so the next failing run proposes a fresh one`;
  }
  if (metadata.dependabot && dependabotPull) {
    const { number, sha } = metadata.dependabot;
    if (dependabotPull.state !== 'open') {
      return `Dependabot #${number} is ${dependabotPull.merged_at ? 'merged' : 'closed'}; this repair took its package versions from it`;
    }
    if (dependabotPull.head?.sha && dependabotPull.head.sha !== sha) {
      return `Dependabot #${number} moved from ${short(sha)} to ${short(dependabotPull.head.sha)}; the repair is recomputed from its next failing run`;
    }
  }
  return null;
}

/** Closes every open maintenance proposal that can no longer merge, with a note saying why. Returns what it closed. */
export function closeStaleMaintenanceProposals({ api, repository }) {
  const mainSha = api(`repos/${repository}/branches/main`).commit.sha;
  const closed = [];
  for (const pull of api(`repos/${repository}/pulls?state=open&base=main&per_page=100`)) {
    if (pull.state !== 'open' || pull.user?.login !== 'github-actions[bot]' || !pull.head?.ref?.startsWith('connie/maintenance/')) continue;
    const metadata = maintenanceMetadata(api(`repos/${repository}/commits/${pull.head.sha}`).commit.message);
    if (!metadata) continue;
    const dependabotPull = metadata.dependabot ? api(`repos/${repository}/pulls/${metadata.dependabot.number}`) : undefined;
    const reason = staleMaintenanceReason({ metadata, mainSha, dependabotPull });
    if (!reason) continue;
    api(`repos/${repository}/issues/${pull.number}/comments`, { method: 'POST', body: { body: `Closed as stale: ${reason}.` } });
    api(`repos/${repository}/pulls/${pull.number}`, { method: 'PATCH', body: { state: 'closed' } });
    closed.push({ number: pull.number, reason });
  }
  return closed;
}
