import { parseWorkflow } from './workflow-policy.mjs';
import { maintenanceMetadata, validateRepairFiles } from './maintenance-policy.mjs';

export const requiredJobs = ['Rust quality gate', 'Repo checks', 'Security checks', 'Build (Windows x64)', 'WinUI host', 'Installer verification / Windows NSIS and MSI smoke', 'CI required'];

export function dependencyUpdateType(message) {
  const start = message.indexOf('\nupdated-dependencies:\n');
  if (start < 0) return null;
  try {
    const block = message.slice(start + 1).split(/\n\.\.\.(?:\n|$)/)[0];
    const dependencies = parseWorkflow(block)['updated-dependencies'];
    if (!Array.isArray(dependencies) || !dependencies.length) return null;
    return dependencies.every(item => ['version-update:semver-patch', 'version-update:semver-minor'].includes(item['update-type']))
      ? 'compatible' : 'review';
  } catch { return null; }
}

export function safeDependencyFile(file) {
  if (file.status !== 'modified' || !file.patch) return false;
  if (['Cargo.lock', 'package-lock.json'].includes(file.filename) || /^src-winui\/[^/]+\/packages\.lock\.json$/.test(file.filename)) return true;
  const changes = file.patch.split('\n').filter(line => /^[+-](?![+-])/.test(line)).map(line => line.slice(1));
  if (!changes.length) return false;
  const sameApartFromVersions = normalize => {
    const lines = sign => file.patch.split('\n').filter(line => line.startsWith(sign) && !line.startsWith(sign.repeat(3)))
      .map(line => normalize(line.slice(1).trim())).sort();
    return JSON.stringify(lines('+')) === JSON.stringify(lines('-'));
  };
  if (/^\.github\/(?:workflows\/[^/]+\.ya?ml|actions\/[^/]+\/action\.ya?ml)$/.test(file.filename)) {
    if (!changes.every(line => /^\s*(?:-\s+)?uses:\s*[\w/-]+@[\w./-]+(?:\s+#.*)?$/.test(line))) return false;
    const normalized = sign => file.patch.split('\n').filter(line => line.startsWith(sign) && !line.startsWith(sign.repeat(3)))
      .map(line => line.slice(1).trim().replace(/@[^\s]+/, '@VERSION')).sort();
    return JSON.stringify(normalized('+')) === JSON.stringify(normalized('-'));
  }
  if (/^src-winui\/[^/]+\/[^/]+\.csproj$/.test(file.filename)) {
    return changes.every(line => /^\s*<PackageReference\s+Include="[\w.]+"\s+Version="[\d.]+"\s*(?:\/>|>)\s*$/.test(line))
      && sameApartFromVersions(line => line.replace(/Version="[\d.]+"/, 'Version="VERSION"'));
  }
  if (/^crates\/[^/]+\/Cargo\.toml$/.test(file.filename)) {
    return changes.every(line => /^\s*[\w-]+\s*=\s*(?:"[\d.^~<>=* ,]+"|\{[^\n]*\bversion\s*=\s*"[\d.^~<>=* ,]+"[^\n]*\})\s*$/.test(line))
      && sameApartFromVersions(line => line.replace(/"[\d.^~<>=* ,]+"/g, '"VERSION"'));
  }
  if (file.filename === 'package.json') return changes.every(line => /^\s*"[@\w/.-]+":\s*"[\d.^~<>=* |]+",?\s*$/.test(line))
    && sameApartFromVersions(line => line.replace(/:\s*"[\d.^~<>=* |]+"/, ': "VERSION"'));
  return false;
}

export function evaluateMerge({ repository, pr, run, jobs, protection, commit, files, repairVerified = false }) {
  const deny = reason => ({ eligible: false, reason });
  if (pr.state !== 'open' || pr.draft || pr.base.ref !== 'main' || pr.head.repo?.full_name !== repository) return deny('PR must be open, ready and from this repository into main');
  const maintenance = maintenanceMetadata(commit.commit.message);
  const bot = maintenance ? 'github-actions[bot]' : 'dependabot[bot]';
  if (pr.user.login !== bot || commit.author?.login !== bot) return deny('Unexpected PR or commit author');
  if (run.path !== '.github/workflows/ci.yml' || run.name !== 'CI' || !(maintenance ? ['workflow_dispatch', 'pull_request'] : ['pull_request']).includes(run.event)
      || run.status !== 'completed' || run.conclusion !== 'success') return deny('Expected a successful completed PR CI run');
  if (run.head_sha !== pr.head.sha || commit.sha !== pr.head.sha) return deny('CI or commit does not match the current PR head');
  if (run.head_repository?.full_name !== repository) return deny('CI originated outside this repository');
  if (!protection?.required_status_checks?.strict || !protection?.enforce_admins?.enabled
      || !protection.required_status_checks.contexts?.includes('CI required')) return deny('Enforced, up-to-date branch protection is required');
  for (const name of requiredJobs) {
    const matches = jobs.filter(job => job.name === name);
    if (matches.length !== 1 || matches[0].conclusion !== 'success') return deny(`Required job is not successful: ${name}`);
  }
  if (pr.mergeable !== true || pr.mergeable_state !== 'clean') return deny('PR must be mergeable and up to date with all required checks');
  if (maintenance) {
    if (maintenance.recipe === 'release') return deny('Release version decisions require human review');
    if (!pr.head.ref?.startsWith(`connie/maintenance/${maintenance.recipe}-`) || commit.parents?.length !== 1
        || commit.parents[0].sha !== maintenance.source || pr.base.sha !== maintenance.source) return deny('Repair must be a single commit on current main');
    if (!validateRepairFiles(maintenance.recipe, files) || !repairVerified) return deny('Repair must match the deterministic recipe and bounded allowlist');
    return { eligible: true, reason: 'Verified maintenance recipe passed the complete current-head CI gates' };
  }
  if (dependencyUpdateType(commit.commit.message) !== 'compatible') return deny('Major, unknown or incompatible updates require review');
  if (!files.length || files.length > 30 || files.some(file => !safeDependencyFile(file))) return deny('Changes exceed the dependency-only allowlist');
  return { eligible: true, reason: 'Current PR head passed every required gate with an eligible dependency-only update' };
}
