# CI maintenance

Local preparation does not activate GitHub workflows, branch protection, or
repository settings merely by editing these files.
No push, PR, remote settings change, or release is part of local verification.

## Why apparently random failures occurred

The UI smoke test requested a 1920-pixel window, but the hosted desktop could
constrain its actual size. Both panes stayed below the Details layout threshold,
so the test incorrectly concluded that Details had never been exercised. The
v1.0.2 test measures actual pane widths, verifies Details in narrow dual panes,
and separately exercises Details in a full-width single pane. Use
`npm run smoke:winui-ui -- -Counts 10000 -MaxWindowWidth 1000` to reproduce the
constrained desktop case locally. Details stays selected at narrow widths.

Several repository guards also depended on spelling: an action version, the
name of a hash implementation, or generic provider words in documentation.
Workflow checks now parse YAML and validate job dependencies, permissions,
commands, and action consistency. Hashing has a behavioral check. Provider
checks target retired executable integration identifiers and dependencies.

Compiler/API changes, failing behavior tests, security advisories, and conflicting
release versions are real failures. The maintenance policy does not suppress
them or weaken their checks to make a build green.

The v1.0.2 installer UI smoke retains both 10,000- and 100,000-entry rendering
checks, while transfer regressions use the smallest requested fixture. This
avoids coupling the ten-minute smoke budget to a 100,000-file durable-copy
stress run. Atomic `progress.json` checkpoints identify the active scenario
and survive timeouts alongside completed results. See
[WinUI runtime verification](WINUI_RUNTIME_VERIFICATION.md) for stress commands.

## Update coverage and reproducibility

Dependabot covers Cargo, npm, GitHub Actions, and NuGet. Action updates are
grouped so coordinated changes do not leave inconsistent versions in workflows.
NuGet lockfiles are committed for all four projects; CI restores in locked mode.
Run `dotnet restore src-winui/SimpleFile.sln --force-evaluate` locally when
intentionally updating package references, then review the lockfile changes.

Rust, Node, and .NET have one version source each: `rust-toolchain.toml`,
`.node-version`, and `global.json`. CI uses those sources and `windows-2025`.
The runner image still receives upstream servicing; pinning its OS label does
not freeze every installed component. The weekly maintenance schedule checks
official stable release feeds and prepares a PR for updates within the current
major versions. Node stays on its existing LTS major and .NET on its existing
release channel. A new major version needs a deliberate migration.

Local preview commands do not update files or GitHub:

```powershell
npm ci --ignore-scripts
npm run maintenance:toolchains
npm run maintenance:plan
npm run release:prepare -- 1.0.3
```

Add `--write` to `maintenance:toolchains` or `release:prepare` only to apply that
proposal locally. Release preparation updates the three Cargo manifests,
workspace Cargo.lock and NuGet project-reference entries, WinUI version properties, Rust display version,
Windows assembly manifest, NSIS numeric fallback, and README identity. Release
notes still require editorial review; the release workflow requires a matching
version-specific notes file and publishes it as the release body. A manually
dispatched release derives its version from these files; an optional supplied
version is an assertion and fails if it disagrees. Version preparation never
tags, signs, or publishes a release.

## Bounded repair and merging

`maintenance.yml` examines failed CI/installer runs using the trusted main-branch
policy and actual failed-step logs. It compares failing steps with a run for the
current main commit. Matching steps are evidence of a possible baseline problem,
not proof of an identical root cause.

| Case | Automated action |
| --- | --- |
| Recognized connection/DNS/HTTP 502–504 error in setup/download/cache/restore steps | Retry failed jobs once; a second failure requires review |
| Current main fails its Rust formatting check | Run the pinned formatter and propose a repair PR |
| Current main reports stale IPC/Open With generated files | Regenerate from existing schemas and propose a repair PR |
| Daily security check or an audit-only failure on a current same-repository branch | Re-audit trusted current main and propose Cargo patch updates |
| Weekly toolchain check | Propose stable updates within current major versions |
| Explicit release-version preparation | Propose a consistent version change; human merge required |
| PR code failure, old main failure, fork, mixed security/compiler/test failure, missing logs, or unknown cause | Record the diagnosis in the workflow summary and fail the assessment for attention |

Write-capable jobs only check out an assessed main commit. They never execute
PR code. Repair proposals are restricted to existing files in recipe-specific
allowlists, at most 30 files, 1,500 changed lines, and 1 MB per file. They cannot
delete files, alter tests or workflows as an incidental repair, or add advisory
exceptions. An identical proposal is deduplicated even if its PR was closed.
If main moves, the repair must be recomputed. New main commits can receive new
proposals; this is not an unlimited retry loop on the same failed revision.

PR creation uses the workflow token and explicitly dispatches CI, because
token-created PR events do not start another workflow automatically. Interrupted
publication can recover its branch/PR and missing CI dispatch. No PAT or AI
service key is needed. Unknown fixes are intentionally not guessed.

Runs started with the workflow token also never raise `workflow_run`, so the
merge gate cannot follow that dispatched CI the usual way. Instead, a
successful dispatched CI run on a `connie/maintenance/` branch ends with a
`merge-gate` job (only `actions: write`) that dispatches the trusted merge
workflow from main with its own run id. The merge workflow re-reads that run,
waits for it to complete and applies the same policy as for `workflow_run`.
The PR's own `pull_request` CI run is held at *approval required* because the
PR came from the workflow token; that alone makes GitHub report the PR as
`unstable`. The merge policy accepts `unstable` only when every other check
suite and commit status on the exact head succeeded and the sole outstanding
runs are those approval-held CI runs.

CI runs repository guards, maintenance tests, strict Rust checks, security audit,
backend build, WinUI unit/XAML/UI/hash checks, and the complete installer workflow
for dependency and maintenance PRs. That installer workflow includes package
startup, file operations, 10k/100k UI checks, MSI/NSIS, and upgrade coverage.
`CI required` fails if any required job fails, is cancelled, or is skipped.
Ordinary human PRs retain the regular CI gates; installer verification is also
available by manual CI dispatch and on the nightly schedule.

The trusted merge policy requires the exact current PR head, every required
job, an up-to-date mergeable branch, and enforced branch protection. Dependabot
patch/minor changes must stay within a dependency-only allowlist. Major or
unrecognized dependency updates require review. Maintenance repairs must be a
single commit on current main, match the recipe allowlist, and reproduce exactly
when the recipe is rerun from trusted main. A changing upstream feed can defer a
toolchain merge until a fresh proposal is created. Release-version PRs always
require review. GitHub receives the expected head SHA when merging, closing the
race between verification and merge.
For partial retries, the merge policy uses the latest result for each job across
attempts of the same run and SHA. A newer failure or skip overrides an older
success. See GitHub's [workflow jobs API](https://docs.github.com/en/rest/actions/workflow-jobs#list-jobs-for-a-workflow-run-attempt).

## Security repair boundaries

The security recipe uses the same advisory exceptions and denied-warning policy
as the release audit (`scripts/lib/audit-policy.mjs`). It audits trusted main
again; PR logs supply no package names or executable commands. Only vulnerable, actionable-warning,
or yanked stable crates.io packages are targeted, using non-yanked updates within
the existing major/minor version. Cargo writes the lockfile. Any transitive
changes must also be patch updates; added/removed packages or source changes
require review. A failed lookup, resolution, graph check, or final audit restores
the original lockfile and fails the job. An unchanged, clean main produces no PR.

Only `Cargo.lock` may be proposed. Tests, manifests, workflows, and advisory
exceptions cannot be modified by this recipe. Daily checks also detect advisories
without a new commit. Identical proposals are deduplicated and receive the full CI
and installer gates. The merge job independently replays the security recipe
from trusted main; changes in upstream versions or advisories can defer merging.

To run this locally (writes Cargo.lock when patches are needed):

```powershell
node scripts/update-security.mjs
```

## Activation after local review

A repository permission is separate from the workflow's `pull-requests: write`.
If GitHub returns HTTP 403 saying Actions cannot create or approve pull requests,
enable **Settings → Actions → General → Workflow permissions → Allow GitHub
Actions to create and approve pull requests**. GitHub exposes creation and approval
as one setting; these workflows do not submit approving reviews. This setting
can be repaired even while main is failing or branch protection already exists:

```powershell
node scripts/configure-maintenance.mjs --plan --permissions-only
node scripts/configure-maintenance.mjs --apply --permissions-only
```

Use an authenticated GitHub CLI with repository Administration write access.
The command preserves default token permissions and all branch protection,
verifies the setting after applying it, and is safe to repeat. Organization
policy can block activation. The workflow token cannot grant itself this setting.
The PR creation error now provides the exact setting and recovery command.
An interrupted proposal can recover its existing branch on the next run.

After this PR is merged and `CI required` is green on main, full setup is available
with `node scripts/configure-maintenance.mjs --apply`. Existing protection is
validated and preserved, including extra checks/review requirements. If absent,
the baseline is installed only after a successful main gate. Incompatible existing
protection requires manual review. Repository auto-merge settings are unchanged;
the trusted merge workflow performs its own guarded merge.

Exercise the security recipe through **Bounded maintenance → Run workflow →
security** after activation and inspect its audit, PR, dispatched CI, installer
results and merge decision. Local mocked tests cannot establish hosted permissions.

Automatic merging refuses to proceed until protection is verifiable. There is
no safe promise of completely unattended fixes for arbitrary future bugs or
breaking changes; this setup automates the bounded, testable maintenance cases
and surfaces the rest.

## Local verification

```powershell
npm run check
npm run test:maintenance
actionlint
dotnet restore src-winui/SimpleFile.sln --locked-mode
npm run check:winui
npm run build:winui
npm run check:release
npm run check:winui-release-hash
npm run smoke:winui-ui -- -Counts 10000 -MaxWindowWidth 1000
npm run smoke:winui-ui
```

Run the release build and packaged smoke checks when changing package or
toolchain behavior. Test reports belong under `test-results/`; generated
installers remain local under `dist/winui/` until explicitly published.
