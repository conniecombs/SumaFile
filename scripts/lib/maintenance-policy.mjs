export const generatedFiles = [
  'src-winui/SimpleFile.Ipc/Protocol.Generated.cs',
  'src-winui/SimpleFile.Ipc/NamedPipeJsonClient.Generated.cs',
  'crates/simplefile-ipc/src/protocol_generated.rs',
  'src-winui/SimpleFile.Core/OpenWithPolicy.Generated.cs',
  'crates/simplefile-core/src/open_with_policy_generated.rs',
];
export const releaseFiles = ['README.md', 'Cargo.lock', 'src-winui/Directory.Build.props',
  'src-winui/SimpleFile.App/app.manifest', 'packaging/winui/sumafile-winui.nsi',
  'crates/simplefile-core/src/lib.rs', ...['core', 'ipc', 'service'].map(name => `crates/simplefile-${name}/Cargo.toml`),
  ...['App', 'Core', 'Ipc', 'Tests'].map(name => `src-winui/SimpleFile.${name}/packages.lock.json`)];
export const recipes = ['rustfmt', 'generated', 'toolchains', 'release', 'security', 'nuget-locks'];
export const limits = { files: 30, changedLines: 1500, fileBytes: 1_000_000 };

export function allowedRepairPath(recipe, path) {
  if (typeof path !== 'string' || path.includes('..') || path.includes('\\') || path.startsWith('/')) return false;
  if (recipe === 'security') return path === 'Cargo.lock';
  if (recipe === 'generated') return generatedFiles.includes(path);
  if (recipe === 'rustfmt') return /^crates\/simplefile-(core|ipc|service)\/(src|tests)\/[\w/.-]+\.rs$/.test(path);
  if (recipe === 'toolchains') return ['rust-toolchain.toml', 'global.json', '.node-version'].includes(path);
  if (recipe === 'release') return releaseFiles.includes(path);
  // The .NET projects' own files and their lock files, which the solution-wide restore rewrites.
  if (recipe === 'nuget-locks') {
    return /^(?:src-winui\/[\w.-]+|src-winui\/SimpleFile\.Tests\/Fixtures\/HelloPlugin|plugins\/(?:git|remote)\/ui\/[\w.-]+|templates\/sumafile-plugin\/ui\/[\w.-]+)\/(?:[\w.-]+\.csproj|packages\.lock\.json)$/.test(path);
  }
  return false;
}

export function validateRepairFiles(recipe, files) {
  if (!recipes.includes(recipe) || !files.length || files.length > limits.files) return false;
  if (new Set(files.map(file => file.filename)).size !== files.length) return false;
  return files.every(file => file.status === 'modified' && allowedRepairPath(recipe, file.filename)
    && Number.isInteger(file.additions) && Number.isInteger(file.deletions)
    && file.additions >= 0 && file.deletions >= 0)
    && files.reduce((total, file) => total + file.additions + file.deletions, 0) <= limits.changedLines;
}

export const failedSteps = jobs => jobs.filter(job => job.conclusion === 'failure' && job.name !== 'CI required')
  .flatMap(job => (job.steps ?? []).filter(step => step.conclusion === 'failure').map(step => `${job.name}: ${step.name}`)).sort();

export function classifyFailure({ run, jobs, log, repository, mainSha, branchHead = mainSha, baseJobs = [] }) {
  const steps = failedSteps(jobs);
  const baseline = steps.length && JSON.stringify(steps) === JSON.stringify(failedSteps(baseJobs))
    ? 'main has the same failing steps' : 'no matching main failure established';
  const review = reason => ({ action: 'review', reason, baseline, failedSteps: steps });
  if (run.head_repository?.full_name !== repository) return review('Fork runs require review');
  if (!['CI', 'Installer smoke'].includes(run.name) || !['.github/workflows/ci.yml', '.github/workflows/installer-smoke.yml'].includes(run.path)
      || run.status !== 'completed' || run.conclusion !== 'failure') return { action: 'none', reason: 'Not a supported failed run' };
  if (!steps.length || typeof log !== 'string' || !log.trim()) return review('Failure evidence is incomplete');
  if (branchHead !== run.head_sha) return review('Run no longer matches its current branch head');
  // Even a failed PR only triggers a fresh audit of trusted current main.
  // Never derive package names, versions or commands from PR logs.
  if (steps.length === 1 && steps[0] === 'Security checks: Audit Rust dependencies'
      && /RUSTSEC-\d{4}-\d+|vulnerabilit(?:y|ies) found|denied warning found/i.test(log)) {
    return { action: 'prepare', recipe: 'security', reason: 'Re-audit current main and propose bounded Cargo patches', baseline, failedSteps: steps };
  }
  // Dependabot's NuGet update regenerated one project's lock file but not those of the projects that reference it.
  // The repair is built from current main with the pull request's versions; its code is never run.
  const dependabotNuget = /^dependabot\/nuget\//.test(run.head_branch) && run.pull_requests?.length === 1
    && Number.isInteger(run.pull_requests[0]?.number);
  if (dependabotNuget && /error NU1004\b/.test(log) && steps.every(step => step.startsWith('WinUI host:'))) {
    return { action: 'prepare', recipe: 'nuget-locks', pr: run.pull_requests[0].number,
      reason: 'Restore the lock files of the projects that reference the updated one', baseline, failedSteps: steps };
  }
  // A deterministic error wins even when an unrelated download also failed.
  if (/error\[E\d+\]|error (?:CS|NU)\d+|test result: FAILED|Failed!\s+-\s+Failed:|panicked at|Assertion failed|RUSTSEC-\d{4}-\d+|vulnerabilit(?:y|ies) found/i.test(log)) {
    return review('Compiler, assertion, package or security failure requires review');
  }
  const transient = /ECONNRESET|EAI_AGAIN|ETIMEDOUT|HTTP(?:Error| error|\/\S+)?[: ]+50[234]|response status code does not indicate success:\s*50[234]|remote name could not be resolved|temporary failure in name resolution|runner lost communication|connection reset by peer/i.test(log);
  const infrastructureSteps = steps.every(step => /Checkout|Setup |Install |Cache |Download |Restore |Audit Rust dependencies/.test(step));
  if (transient && infrastructureSteps) return run.run_attempt === 1
    ? { action: 'retry', reason: 'Known transport failure; one retry permitted', baseline, failedSteps: steps }
    : review('Transport failure persisted after the single permitted retry');
  // Never execute PR code with a write token. Failed PRs receive diagnosis;
  // deterministic repair branches are built only from the current default branch.
  if (run.head_branch !== 'main' || run.head_sha !== mainSha) return review('PR or superseded main run: report for review without rewriting its code');
  if (steps.length === 1 && /Check formatting$/.test(steps[0]) && /Diff in .*\.rs(?::\d+)?:/.test(log)) {
    return { action: 'prepare', recipe: 'rustfmt', reason: 'Pinned formatter can repair this failure', baseline, failedSteps: steps };
  }
  if (steps.length === 1 && /Run repo checks$/.test(steps[0])
      && /(?:is stale\. Run npm run generate:ipc-bindings\.|is out of date\. Run npm run generate:open-with-policy\.)/.test(log)) {
    return { action: 'prepare', recipe: 'generated', reason: 'Regenerate checked-in output from the existing schema', baseline, failedSteps: steps };
  }
  return review('No deterministic repair recipe matches this failure');
}

export function maintenanceMetadata(message) {
  const recipe = /^SumaFile-Maintenance: ([\w-]+)$/m.exec(message)?.[1];
  const source = /^SumaFile-Source: ([a-f0-9]{40})$/m.exec(message)?.[1];
  if (!recipes.includes(recipe) || !source) return null;
  const provenance = /^SumaFile-Dependabot: (\d+)@([a-f0-9]{40})$/m.exec(message);
  if (recipe === 'nuget-locks') {
    // A lock-file repair without the pull request it took its versions from cannot be replayed.
    return provenance ? { recipe, source, dependabot: { number: Number(provenance[1]), sha: provenance[2] } } : null;
  }
  return { recipe, source };
}
