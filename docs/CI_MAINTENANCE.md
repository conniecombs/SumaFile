# CI maintenance

Local preparation does not activate GitHub workflows, branch protection, or
repository settings merely by editing these files.
No push, PR, remote settings change, or release is part of local verification.

## Why apparently random failures occurred

The UI smoke test requested a 1920-pixel window, but the hosted desktop could
constrain its actual size. Both panes stayed below the Details layout threshold,
so the test incorrectly concluded that Details had never been exercised. The
test now measures actual pane widths, verifies the narrow dual-pane fallback,
and separately exercises Details in a full-width single pane. Use
`npm run smoke:winui-ui -- -Counts 10000 -MaxWindowWidth 1000` to reproduce the
constrained desktop case locally. The product's layout threshold is unchanged.

Several repository guards also depended on spelling: an action version, the
name of a hash implementation, or generic provider words in documentation.
Workflow checks now parse YAML and validate job dependencies, permissions,
commands, and action consistency. Hashing has a behavioral check. Provider
checks target retired executable integration identifiers and dependencies.

Compiler/API changes, failing behavior tests, security advisories, and conflicting
release versions are real failures. The maintenance policy does not suppress
them or weaken their checks to make a build green.

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
npm run release:prepare -- 1.0.2
```

Add `--write` to `maintenance:toolchains` or `release:prepare` only to apply that
proposal locally. Release preparation updates the three Cargo manifests,
workspace Cargo.lock and NuGet project-reference entries, WinUI version properties, Rust display version,
and README identity. Release notes still require editorial review. A manually
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
| Weekly toolchain check | Propose stable updates within current major versions |
| Explicit release-version preparation | Propose a consistent version change; human merge required |
| PR code failure, old main failure, fork, security/compiler/test failure, missing logs, or unknown cause | Record the diagnosis in the workflow summary and fail the assessment for attention |

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

## Activation after local review

These are future deployment steps, not actions performed by local preparation:

1. Publish the reviewed files and obtain a successful `CI required` run on main.
2. Review `npm run maintenance:plan` and any existing organization/repository
   rules. The plan enables strict required checks, administrator enforcement,
   blocks force pushes/deletion, and permits workflow PR creation. Its zero
   required review count allows explicitly eligible bot updates to merge;
   unknown and major updates still need a human decision.
3. With repository administration access, deliberately run
   `node scripts/configure-maintenance.mjs --apply`. This is a remote mutation.
   It refuses to overwrite existing protection, requires the successful gate
   first, and verifies the resulting required-check rule. Organization policy
   can prevent a repository setting from taking effect.
4. Exercise a toolchain proposal or controlled repair in GitHub and inspect its
   dispatched CI, installer evidence, and merge decision. Local tests mock API
   writes; they do not prove hosted permissions or end-to-end event delivery.

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
