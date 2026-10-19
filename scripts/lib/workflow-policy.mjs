import { parseDocument } from 'yaml';

export function parseWorkflow(source) {
  const document = parseDocument(source, { uniqueKeys: true, version: '1.2' });
  if (document.errors.length) throw new Error(document.errors.map(error => error.message).join('\n'));
  return document.toJS({ maxAliasCount: 100 });
}

const list = value => value == null ? [] : Array.isArray(value) ? value : [value];
const steps = job => job?.steps ?? [];
const runs = job => steps(job).flatMap(step => (step.run ?? '').split(/\r?\n/))
  .map(line => line.trim()).filter(line => line && !line.startsWith('#'));
const action = (job, name) => steps(job).find(step => step.uses?.startsWith(`${name}@`));

export function validateWorkflows(workflows, dependabot) {
  const findings = [];
  const check = (condition, message) => { if (!condition) findings.push(message); };
  const job = (file, id) => {
    const result = workflows[file]?.jobs?.[id];
    check(result, `${file}: missing job ${id}`);
    return result;
  };
  const command = (file, id, pattern, description) => {
    check(runs(workflows[file]?.jobs?.[id]).some(line => pattern.test(line)), `${file}/${id}: missing ${description}`);
  };
  const needs = (file, id, required) => {
    const actual = list(workflows[file]?.jobs?.[id]?.needs);
    for (const dependency of required) check(actual.includes(dependency), `${file}/${id}: must depend on ${dependency}`);
  };
  const refs = new Map();
  for (const [file, workflow] of Object.entries(workflows)) {
    check(workflow && typeof workflow === 'object' && workflow.on && workflow.jobs, `${file}: triggers and jobs are required`);
    check(workflow.permissions && workflow.permissions !== 'write-all', `${file}: declare scoped permissions`);
    for (const [id, current] of Object.entries(workflow.jobs ?? {})) {
      check(current['continue-on-error'] !== true, `${file}/${id}: cannot ignore job failure`);
      for (const step of steps(current)) {
        check(step['continue-on-error'] !== true, `${file}/${id}/${step.name}: cannot ignore step failure`);
        if (!step.uses || step.uses.startsWith('./')) continue;
        const match = /^([^@]+)@([\w./-]+)$/.exec(step.uses);
        check(match, `${file}/${id}: invalid action reference ${step.uses}`);
        if (!match) continue;
        const [, name, ref] = match;
        if (name.startsWith('actions/')) {
          check(/^v\d+(?:\.\d+){0,2}$|^[a-f0-9]{40}$/i.test(ref), `${file}/${id}: ${name} needs a version or commit pin`);
          const versions = refs.get(name) ?? new Set();
          versions.add(ref);
          refs.set(name, versions);
        }
      }
      for (const line of runs(current)) {
        check(!/--ignore\s+RUSTSEC-|libwebkit2gtk|tauri-action/.test(line), `${file}/${id}: retired or bypassed release check`);
      }
    }
  }
  // Dependabot owns action versions in YAML. Validate consistency rather than
  // keeping another hard-coded copy of the versions in JavaScript.
  for (const [name, versions] of refs) check(versions.size === 1, `${name}: inconsistent refs ${[...versions].join(', ')}`);

  const ci = workflows['ci.yml'];
  for (const event of ['push', 'pull_request']) {
    for (const branch of ['main', 'C#']) check(list(ci?.on?.[event]?.branches).includes(branch), `CI ${event}: missing ${branch}`);
  }
  check(Object.hasOwn(ci?.on ?? {}, 'workflow_dispatch'), 'CI: manual dispatch is required');
  check(ci?.permissions?.contents === 'read', 'CI: contents must be read-only');
  const quality = job('ci.yml', 'quality');
  check(String(quality?.['runs-on']).startsWith('windows-'), 'Rust quality gate must run on Windows');
  command('ci.yml', 'quality', /^cargo fmt\b.*--check/, 'format check');
  command('ci.yml', 'quality', /^cargo clippy\b.*--locked.*--all-targets.*--all-features.*-D warnings/, 'strict Clippy');
  command('ci.yml', 'quality', /^cargo test\b.*--locked.*--all-features/, 'locked Rust tests');
  command('ci.yml', 'repo-checks', /^npm run check\s*$/, 'repository checks');
  command('ci.yml', 'repo-checks', /^npm ci\b/, 'locked JavaScript install');
  command('ci.yml', 'security', /^node scripts\/cargo-audit-release\.mjs$/, 'Rust security audit');
  command('ci.yml', 'build', /^cargo build\b.*simplefile-service.*--locked.*--release/, 'backend build');
  needs('ci.yml', 'build', ['quality', 'repo-checks', 'security']);
  needs('ci.yml', 'winui', ['quality']);
  for (const [pattern, description] of [
    [/^npm run check:winui\s*$/, 'WinUI unit tests'],
    [/^npm run build:winui\s*$/, 'XAML application build'],
    [/scripts\/check-winui-release-hash\.ps1/, 'behavioral release hashing check'],
    [/^npm run smoke:winui-ui\b/, 'real application UI smoke'],
    [/^node scripts\/check-winui-packaging\.mjs$/, 'packaging checks'],
  ]) command('ci.yml', 'winui', pattern, description);
  const reports = action(ci?.jobs?.winui, 'actions/upload-artifact');
  check(reports?.if === 'always()' && reports?.with?.path?.includes('report.json'), 'CI: retain runtime evidence on failure');
  needs('ci.yml', 'required', ['quality', 'repo-checks', 'security', 'build', 'winui', 'installer']);
  check(ci?.jobs?.required?.name === 'CI required' && ci.jobs.required.if === 'always()', 'CI: aggregate required gate must always run');
  check(ci?.jobs?.installer?.uses === './.github/workflows/installer-smoke.yml', 'CI: installer verification must use the complete smoke workflow');

  const release = workflows['release.yml'];
  check(list(release?.on?.push?.tags).includes('v*'), 'Release: version tag trigger required');
  check(release?.on?.workflow_dispatch?.inputs?.version?.required === false, 'Release: manual version must be optional');
  job('release.yml', 'validate');
  command('release.yml', 'validate', /^node scripts\/release-version\.mjs --resolve$/, 'manifest-derived release version');
  needs('release.yml', 'checks', ['validate']);
  needs('release.yml', 'build', ['validate', 'checks']);
  needs('release.yml', 'publish', ['validate', 'build']);
  check(release?.jobs?.publish?.if === "needs.validate.outputs.draft == 'false'", 'Release: publication must honor draft selection');
  command('release.yml', 'checks', /^npm run check\s*$/, 'repository checks');
  command('release.yml', 'checks', /^npm ci\b/, 'locked JavaScript install');
  command('release.yml', 'checks', /^cargo fmt\b.*--check/, 'format check');
  command('release.yml', 'checks', /^cargo clippy\b.*--locked.*-D warnings/, 'strict Clippy');
  command('release.yml', 'checks', /^cargo test\b.*--locked/, 'Rust tests');
  command('release.yml', 'checks', /^node scripts\/cargo-audit-release\.mjs$/, 'security audit');
  command('release.yml', 'build', /build-winui-release\.ps1.*-RequireInstaller.*-RequireUpdaterSignature/, 'signed installer build');
  check(action(release?.jobs?.build, 'softprops/action-gh-release')?.with?.fail_on_unmatched_files === true,
    'Release: fail if expected assets are absent');

  for (const [file, id] of [['release.yml', 'build'], ['release-build.yml', 'windows-release-build'], ['installer-smoke.yml', 'windows-installer-smoke']]) {
    const current = job(file, id);
    check(action(current, 'actions/checkout')?.with?.['fetch-depth'] === 0, `${file}: upgrade smoke requires full history`);
    command(file, id, /build-winui-release\.ps1.*-RequireInstaller/, 'installer build');
    command(file, id, /choco install wixtoolset\b/, 'WiX bootstrap');
    command(file, id, /choco install nsis\b/, 'NSIS bootstrap');
    command(file, id, /^npm run smoke:winui-file-ops\s*$/, 'packaged file operations');
    command(file, id, /^npm run smoke:winui-upgrade\s*$/, 'published-version upgrade smoke');
    command(file, id, /^npm run smoke:winui-upgrade-from-ref\b/, 'baseline upgrade smoke');
    check(action(current, 'actions/setup-dotnet'), `${file}: explicit .NET setup required`);
    const upload = action(current, file === 'release.yml' ? 'softprops/action-gh-release' : 'actions/upload-artifact');
    check(upload, `${file}: artifact upload is required`);
  }
  check(list(workflows['installer-smoke.yml']?.on?.schedule).length > 0, 'Installer smoke: schedule required');
  check(Object.hasOwn(workflows['installer-smoke.yml']?.on ?? {}, 'workflow_call'), 'Installer smoke must be callable from CI');
  command('installer-smoke.yml', 'windows-installer-smoke', /^npm run smoke:winui-ui\s*$/, 'full UI smoke');
  command('installer-smoke.yml', 'windows-installer-smoke', /^npm run smoke:winui-msi\s*$/, 'MSI smoke');
  command('installer-smoke.yml', 'windows-installer-smoke', /^npm run smoke:winui-installer\s*$/, 'NSIS smoke');

  const merge = workflows['dependabot-automerge.yml'];
  check(list(merge?.on?.workflow_run?.workflows).includes('CI') && list(merge?.on?.workflow_run?.types).includes('completed'),
    'Dependency merging must follow completed CI');
  check(merge?.permissions?.contents === 'write' && merge?.permissions?.['pull-requests'] === 'write', 'Dependency merging needs scoped write permissions');
  check(action(merge?.jobs?.automerge, 'actions/checkout')?.with?.ref === '${{ github.event.repository.default_branch }}',
    'Automerge must execute trusted default-branch policy');
  check(action(merge?.jobs?.automerge, 'actions/checkout')?.with?.['persist-credentials'] === false, 'Automerge checkout must not retain credentials');
  command('dependabot-automerge.yml', 'automerge', /^node scripts\/automerge-dependencies\.mjs$/, 'current-head merge policy');
  const maintenance = workflows['maintenance.yml'];
  check(list(maintenance?.on?.workflow_run?.workflows).includes('CI') && list(maintenance?.on?.schedule).length > 0, 'Maintenance: failure and scheduled triggers required');
  check(action(maintenance?.jobs?.assess, 'actions/checkout')?.with?.ref === '${{ github.event.repository.default_branch }}', 'Maintenance assessment must use trusted policy');
  check(action(maintenance?.jobs?.prepare, 'actions/checkout')?.with?.ref === '${{ needs.assess.outputs.source }}', 'Maintenance repair must use the assessed default-branch commit');
  command('maintenance.yml', 'assess', /^node scripts\/plan-maintenance\.mjs$/, 'bounded failure classification');
  command('maintenance.yml', 'prepare', /^node scripts\/publish-maintenance\.mjs$/, 'bounded PR publication');
  for (const [ecosystem, directory] of [['cargo', '/'], ['github-actions', '/'], ['npm', '/'], ['nuget', '/src-winui']]) {
    check(dependabot?.updates?.some(update => update['package-ecosystem'] === ecosystem && update.directory === directory), `Dependabot: missing ${ecosystem}`);
  }
  return findings;
}
