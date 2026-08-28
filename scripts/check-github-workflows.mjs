import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');

function readText(relativePath) {
    return fs.readFileSync(path.join(repoRoot, relativePath), 'utf8');
}

function fail(message) {
    console.error(`GitHub workflow check failed: ${message}`);
    process.exitCode = 1;
}

function requireSnippet(source, file, snippet) {
    if (!source.includes(snippet)) {
        fail(`${file} must include ${snippet}.`);
    }
}

function requireRegex(source, file, pattern, label) {
    if (!pattern.test(source)) {
        fail(`${file} must include ${label}.`);
    }
}

function requireOccurrenceCount(source, file, snippet, expectedCount) {
    const actualCount = source.split(snippet).length - 1;
    if (actualCount !== expectedCount) {
        fail(`${file} must include ${snippet} exactly ${expectedCount} time(s), found ${actualCount}.`);
    }
}

const ciPath = '.github/workflows/ci.yml';
const releasePath = '.github/workflows/release.yml';
const releaseBuildPath = '.github/workflows/release-build.yml';
const installerSmokePath = '.github/workflows/installer-smoke.yml';
const dependabotPath = '.github/dependabot.yml';
const dependabotTriagePath = '.github/workflows/dependabot-automerge.yml';

const ciWorkflow = readText(ciPath);
const releaseWorkflow = readText(releasePath);
const releaseBuildWorkflow = readText(releaseBuildPath);
const installerSmokeWorkflow = readText(installerSmokePath);
const dependabot = readText(dependabotPath);
const dependabotTriageWorkflow = readText(dependabotTriagePath);

const ciSnippets = [
    'pull_request:',
    'workflow_dispatch:',
    'permissions:',
    'contents: read',
    'pull-requests: read',
    'uses: actions/checkout@v7',
    'uses: dtolnay/rust-toolchain@stable',
    'components: rustfmt, clippy',
    'uses: actions/setup-node@v7',
    'node-version: 24',
    'npm run check',
    'cargo fmt --all -- --check',
    'cargo clippy --locked --all-targets --all-features -- -D warnings',
    'cargo test --locked --all-features',
    'node scripts/cargo-audit-release.mjs',
    'x86_64-pc-windows-msvc',
    'cargo build -p simplefile-service --locked --release --target ${{ matrix.target }}',
    'uses: actions/setup-dotnet@v4',
    'dotnet-version: 10.0.x',
    'npm run check:winui',
    'cargo build -p simplefile-service --locked --release',
];

for (const snippet of ciSnippets) {
    requireSnippet(ciWorkflow, ciPath, snippet);
}

requireOccurrenceCount(ciWorkflow, ciPath, "branches: [main, 'C#']", 2);

const releaseSnippets = [
    'tags:',
    "'v*'",
    'workflow_dispatch:',
    'contents: write',
    'Validate release version',
    'Version must look like v1.0.0 or v1.0.0-beta.1',
    'Directory.Build.props',
    'crates/simplefile-service/Cargo.toml',
    'components: rustfmt, clippy',
    'uses: actions/setup-node@v7',
    'node-version: 24',
    'npm run check',
    'cargo fmt --all -- --check',
    'cargo clippy --locked --all-targets --all-features -- -D warnings',
    'cargo test --locked --all-features',
    'node scripts/cargo-audit-release.mjs',
    'Install WiX Toolset (MSI)',
    'function Resolve-WixBin',
    'build-winui-release.ps1',
    'RequireUpdaterSignature',
    'SIMPLEFILE_UPDATER_PUBLIC_KEY',
    'smoke:winui-installer',
    'smoke:winui-upgrade',
    'latest-winui.json',
    'x64-winui-portable.zip',
    'x64-winui-setup.exe',
    'x64-winui.msi',
    'release-assets/*',
    'softprops/action-gh-release@v3',
    'fail_on_unmatched_files: true',
    'Windows installer and portable artifacts are attached below:',
];

for (const snippet of releaseSnippets) {
    requireSnippet(releaseWorkflow, releasePath, snippet);
}

const releaseBuildSnippets = [
    'name: Release build',
    'workflow_dispatch:',
    'permissions:',
    'contents: read',
    'runs-on: windows-latest',
    'uses: actions/checkout@v7',
    'uses: dtolnay/rust-toolchain@stable',
    'targets: x86_64-pc-windows-msvc',
    'components: rustfmt, clippy',
    'uses: actions/setup-node@v7',
    'node-version: 24',
    'tool: cargo-audit',
    'dist/winui',
    'build-winui-release.ps1',
    'smoke:winui-upgrade',
    'uses: actions/upload-artifact@v4',
    'retention-days: 30',
];

for (const snippet of releaseBuildSnippets) {
    requireSnippet(releaseBuildWorkflow, releaseBuildPath, snippet);
}

const installerSmokeSnippets = [
    'workflow_dispatch:',
    'schedule:',
    "cron: '0 6 * * *'",
    'permissions:',
    'contents: read',
    'runs-on: windows-latest',
    'uses: actions/checkout@v7',
    'uses: dtolnay/rust-toolchain@stable',
    'uses: actions/setup-node@v7',
    'node-version: 24',
    'smoke:winui',
    'smoke:winui-upgrade',
    'Install WiX Toolset (MSI)',
    'function Resolve-WixBin',
    'Get-Command candle.exe',
    'choco install wixtoolset -y --no-progress',
    'uses: actions/upload-artifact@v4',
];

for (const snippet of installerSmokeSnippets) {
    requireSnippet(installerSmokeWorkflow, installerSmokePath, snippet);
}

requireRegex(
    releaseWorkflow,
    releasePath,
    /if:\s*needs\.validate\.outputs\.draft\s*==\s*'false'/,
    'a publish gate that respects manual draft=false releases',
);

requireRegex(
    ciWorkflow,
    ciPath,
    /quality:[\s\S]*?name:\s*Rust quality gate[\s\S]*?runs-on:\s*windows-latest/,
    'a Windows runner for Rust quality gates',
);

requireRegex(
    releaseWorkflow,
    releasePath,
    /checks:[\s\S]*?name:\s*Release quality gates[\s\S]*?runs-on:\s*windows-latest/,
    'a Windows runner for release quality gates',
);

const retiredWorkflowSnippets = [
    'Install Linux/Tauri dependencies',
    'libwebkit2gtk',
    '--ignore RUSTSEC-',
];

for (const snippet of retiredWorkflowSnippets) {
    if (ciWorkflow.includes(snippet)) {
        fail(`${ciPath} should not include retired workflow snippet: ${snippet}.`);
    }
    if (releaseWorkflow.includes(snippet)) {
        fail(`${releasePath} should not include retired workflow snippet: ${snippet}.`);
    }
    if (installerSmokeWorkflow.includes(snippet)) {
        fail(`${installerSmokePath} should not include retired workflow snippet: ${snippet}.`);
    }
    if (releaseBuildWorkflow.includes(snippet)) {
        fail(`${releaseBuildPath} should not include retired workflow snippet: ${snippet}.`);
    }
}

const retiredArtifactTargets = [
    'x86_64-apple-darwin',
    'aarch64-apple-darwin',
    'x86_64-unknown-linux-gnu',
];

for (const target of retiredArtifactTargets) {
    if (ciWorkflow.includes(target)) {
        fail(`${ciPath} should not build ${target} on the Windows-focused branch.`);
    }
    if (releaseWorkflow.includes(target)) {
        fail(`${releasePath} should not build ${target} on the Windows-focused branch.`);
    }
    if (installerSmokeWorkflow.includes(target)) {
        fail(`${installerSmokePath} should not build ${target} on the Windows-focused branch.`);
    }
    if (releaseBuildWorkflow.includes(target)) {
        fail(`${releaseBuildPath} should not build ${target} on the Windows-focused branch.`);
    }
}

const dependabotSnippets = [
    'package-ecosystem: "cargo"',
    'directory: "/"',
    'package-ecosystem: "github-actions"',
    'directory: "/"',
    'interval: "weekly"',
];

for (const snippet of dependabotSnippets) {
    requireSnippet(dependabot, dependabotPath, snippet);
}

requireOccurrenceCount(dependabot, dependabotPath, 'directory: "/frontend"', 0);

const dependabotTriageSnippets = [
    'name: Dependabot triage',
    'contents: read',
    'issues: write',
    'pull-requests: write',
    'uses: dependabot/fetch-metadata@v3',
    'Label and comment for local review',
    'needs-local-review',
    'gh pr comment "$PR_URL"',
    'Repository auto-merge is disabled',
];

for (const snippet of dependabotTriageSnippets) {
    requireSnippet(dependabotTriageWorkflow, dependabotTriagePath, snippet);
}

if (dependabotTriageWorkflow.includes('gh pr merge --auto')) {
    fail(`${dependabotTriagePath} should triage Dependabot PRs instead of enabling auto-merge.`);
}

if (!process.exitCode) {
    console.log('GitHub workflow configuration is wired.');
}
