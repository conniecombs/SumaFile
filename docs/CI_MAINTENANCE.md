# CI maintenance

This document describes how CI and the maintenance automation work: what each
CI job runs, how dependency and toolchain updates arrive, which failures the
maintenance workflow repairs by itself, and when a pull request is merged
without a person. The release flow is in
[the release runbook](../.github/RELEASE.md).

## CI

`.github/workflows/ci.yml` (**CI**) runs on a push or pull request to `main`
or `C#`, and on manual dispatch.

| Job | What it runs |
| --- | --- |
| Rust quality gate | `cargo fmt --all -- --check`, Clippy with warnings denied, `cargo test --locked --all-features --no-fail-fast` (every test binary runs even after one has failed, so one run shows every failure), and a packaging dry run of the `simplefile-ipc` and `sumafile-plugin` crates. A formatting failure uploads the repair as the `rustfmt-repair` artifact |
| Repo checks | `npm ci --ignore-scripts`, `npm run check`, `npm run test:maintenance` |
| Security checks | The Rust dependency audit, `node scripts/cargo-audit-release.mjs` |
| Build (Windows x64) | A locked release build of `simplefile-service` |
| WinUI host | Builds the service, the Hello fixture plugin and the bundled plugins, tests plugin staging, then runs `npm run check:winui`, `npm run pack:sdk`, `npm run build:winui`, the release-hash check, the 10,000-entry UI smoke with the Hello fixture, the startup budget check, the same UI smoke with `-NoPlugins`, and the packaging check. It uploads the reports and screenshots as the `sumafile-winui-runtime` artifact |
| Installer verification | Calls `installer-smoke.yml`. It runs only for a manual dispatch, a Dependabot pull request, or a branch whose name starts with `connie/maintenance/` |
| CI required | Always runs. It fails unless every job above succeeded; a skipped Installer verification is accepted only on a run that does not require it |
| Hand the run to the merge gate | Runs only for a dispatched run on a `connie/maintenance/` branch whose `CI required` succeeded; see [Merging](#merging) |

The Windows runners are Windows Server, where Media Foundation is an optional feature that is not installed. The
video program (`sumafile-video`) cannot start without it, so the jobs that make video thumbnails (the Rust tests and
the UI smoke, here and in `release.yml` and `installer-smoke.yml`) first run
`.github/actions/setup-media-foundation`. It installs the feature, which takes about three minutes, and fails when
`mfplat.dll` is still missing afterwards.

The branch protection baseline in `.github/branch-protection.json` requires
the `CI required` check on an up-to-date branch.

`.github/workflows/installer-smoke.yml` (**Installer smoke**) also runs daily
at 06:00 UTC and on manual dispatch. It builds unsigned installers and runs
the payload startup smoke, the packaged file-operation smoke, the full UI
smoke (10,000 and 100,000 entries), the MSI extract-and-launch smoke, the NSIS
install smoke, the MSI lifecycle smoke, and the NSIS upgrade smokes from the
latest published release and from a fixed baseline commit.
[WinUI runtime verification](WINUI_RUNTIME_VERIFICATION.md) describes the UI
smoke.

`npm run check:workflows`, part of `npm run check`, parses the workflow files
and `.github/dependabot.yml` and fails when a trigger, job dependency,
required command, permission or action reference no longer matches
`scripts/lib/workflow-policy.mjs`. `npm run test:maintenance` runs the Node
tests under `scripts/tests/`, which cover the maintenance, merge, release and
workflow policies.

A compiler or API error, a failing test, a security advisory without a
bounded patch and a version mismatch are real failures. The automation below
does not suppress them or weaken a check to make a build green.

## Update coverage and reproducibility

Dependabot opens weekly update pull requests for Cargo, npm, GitHub Actions
and NuGet (`.github/dependabot.yml`). Action updates are grouped so that every
workflow moves to the same version together. Cargo and NuGet minor and patch
updates are grouped as well.

Every project in `src-winui/SimpleFile.sln` has a committed
`packages.lock.json`, and CI restores in locked mode
(`src-winui/Directory.Build.props` sets `RestoreLockedMode` when `CI` is
`true`). Run `dotnet restore src-winui/SimpleFile.sln --force-evaluate`
locally when intentionally updating package references, then review the lock
file changes.

Two NuGet rules keep Dependabot's pull requests mergeable without a person:

- `CommunityToolkit.Mvvm`, `Microsoft.Windows.SDK.BuildTools` and
  `Microsoft.WindowsAppSDK` are ignored. `docs/PLUGINS.md` pins their versions
  for plugin authors and
  `PluginsDoc_PinnedVersions_MatchTheVersionsTheHostBuildsWith` holds that table
  to the lock file, so a bump is a plugin-compatibility decision: a person
  changes the version and the table together, in a release.
- Major versions of `System.*` and `Microsoft.Extensions.*` are ignored. Those
  packages follow the .NET runtime's version, so their next major is for the
  next .NET. Remove the rule when the app moves to that .NET.
  `npm run check:dotnet-packages` holds the project files to the same rule.

Dependabot regenerates the lock file of the project whose package it bumps,
but a lock file also records the dependencies of every project it references,
so the referencing projects (the App, for a Core bump) fail CI's locked restore
with NU1004. That failure is repaired by the `nuget-locks` maintenance recipe:
it applies the pull request's package versions to current main, runs the
solution-wide forced restore, and proposes the result as a maintenance pull
request that supersedes Dependabot's. Only version attributes are taken from
the pull request; its code never runs. The merge gate replays the restore from
the same Dependabot head before merging. Restoring on Linux with
`EnableWindowsTargeting` reproduces the Windows lock files byte for byte,
which is what makes the replay possible on the ordinary runners.

Rust, Node and .NET have one version source each: `rust-toolchain.toml`,
`.node-version` and `global.json`. Every workflow installs its toolchains from
those files. The weekly toolchain check reads the official stable release
feeds and proposes updates within the current major versions: Node stays on
the LTS releases of its current major and .NET on its current release channel.
A new major version fails the check and needs a deliberate migration.

These commands preview without changing files or GitHub:

```powershell
npm ci --ignore-scripts
npm run maintenance:toolchains
npm run maintenance:plan
npm run release:prepare -- <version>
```

Add `-- --write` to `maintenance:toolchains` to apply its proposal locally.
Release preparation, the files it rewrites and what is still written by hand
are described in [the release runbook](../.github/RELEASE.md). Version
preparation never tags, signs or publishes a release.

## Bounded repair

`.github/workflows/maintenance.yml` (**Bounded maintenance**) starts when a CI
or Installer smoke run completes, on Mondays at 09:30 UTC, daily at 10:30 UTC,
and on manual dispatch. Its runs never overlap.

For a failed run, the `assess` job checks out the default branch, reads the
failed steps and their logs, and classifies the failure with
`scripts/lib/maintenance-policy.mjs`. It also compares the failing steps with
a CI run for the current main commit. Matching steps are evidence of a
possible baseline problem, not proof of an identical root cause. The
assessment is written to the workflow summary.

| Case | Automated action |
| --- | --- |
| Recognized connection, DNS or HTTP 502–504 error, when every failed step is a checkout, setup, install, cache, download, restore or audit step | Retry the failed jobs once; a second failure requires review |
| Current main fails only its Rust formatting check | Run the pinned formatter and propose a repair (`rustfmt`) |
| Current main fails only its repository checks, reporting stale IPC or Open With generated files | Regenerate from the existing schemas and propose a repair (`generated`) |
| The daily security schedule, or a run on a current same-repository branch whose only failed step is the dependency audit | Re-audit current main and propose Cargo patch updates (`security`) |
| The Monday toolchain schedule | Propose stable updates within the current major versions (`toolchains`) |
| A Dependabot NuGet branch fails the locked restore (NU1004) in the WinUI host job only | Apply its package versions to current main, restore every lock file and propose a superseding pull request (`nuget-locks`) |
| A pull request's code failure, a failure of an old main commit, a fork, a compiler, test, package or security error, missing logs, or an unknown cause | Record the diagnosis and fail the assessment so that it gets attention |

A manual dispatch takes three inputs and prepares the chosen recipe from
current main:

| Input | Meaning |
| --- | --- |
| `recipe` | `security` (the default), `toolchains`, `generated`, `rustfmt`, `release` or `nuget-locks` |
| `version` | The version for `release` |
| `pr` | The Dependabot pull request number for `nuget-locks`; the run fails without it |

The `release` recipe does not currently work: `release:prepare` edits files
that are not in the recipe's allowlist, so publication is refused. Prepare a
release locally as the release runbook describes.

### What a repair may change

The `prepare` job checks out the assessed main commit, never pull request
code (the `nuget-locks` recipe reads a Dependabot pull request's version
attributes, nothing else). It runs the recipe without a token, then
`npm run check` and `npm run test:maintenance`, and publishes the result only
when every changed file is in the recipe's allowlist.

| Recipe | Command | Files it may change |
| --- | --- | --- |
| `rustfmt` | `cargo fmt --all` | Rust files under `src/` and `tests/` of `simplefile-core`, `simplefile-ipc` and `simplefile-service` |
| `generated` | `scripts/generate-ipc-bindings.mjs`, `scripts/generate-open-with-policy.mjs` | The five generated protocol and Open With policy files |
| `toolchains` | `scripts/update-toolchains.mjs --write` | `rust-toolchain.toml`, `global.json`, `.node-version` |
| `security` | `scripts/update-security.mjs` | `Cargo.lock` |
| `nuget-locks` | `dotnet restore src-winui/SimpleFile.sln --force-evaluate -p:EnableWindowsTargeting=true` | The `.csproj` and `packages.lock.json` files of the .NET projects |
| `release` | `scripts/release-version.mjs <version> --write` | The README, `Cargo.lock`, the WinUI version properties, manifest and NSIS script, `simplefile-core`'s `lib.rs`, the manifests of the three `simplefile-*` crates, and the `packages.lock.json` files of the App, Core, Ipc and Tests projects |

`cargo fmt --all` formats every workspace crate, so a formatting failure in a
crate outside those three produces a repair that exceeds the `rustfmt`
allowlist and is not published.

A proposal changes at most 30 files, 1,500 lines in total and 1 MB per file.
It only modifies existing files: it cannot add or delete one, and no allowlist
contains a workflow or the advisory exception list. When main has moved since
the assessment, publication stops and the repair is recomputed on the next
run. New main commits can receive new proposals; this is not an unlimited
retry loop on the same failed revision.

### How a repair is published

A proposal is one commit on the assessed main commit, on the branch
`connie/maintenance/<recipe>-<main commit>-<content fingerprint>` (twelve
characters of each hash), opened as a pull request titled
`Maintenance: <recipe>`. Its commit message is `Maintain <recipe>` followed by
trailers that the merge gate reads:

```text
SumaFile-Maintenance: <recipe>
SumaFile-Source: <main commit>
SumaFile-Dependabot: <pull request number>@<its head commit>
```

The last trailer is written only for `nuget-locks`.

The branch name identifies the proposal, so an identical proposal is not
opened twice, even when its pull request was closed. An interrupted
publication is recovered on the next run: an existing branch gets its pull
request, and an open pull request without a CI run gets one.

Pull request creation uses the workflow token and then dispatches CI on the
branch, because GitHub does not run CI by itself for a pull request opened
with that token: its `pull_request` run is held for approval. No personal
access token is needed. An unknown fix is not guessed.

## Merging

`.github/workflows/dependabot-automerge.yml` (**Validated maintenance
automerge**) is the merge gate. It runs when a CI run completes successfully
for a pull request or a dispatch, checks out the default branch, and applies
`scripts/lib/merge-policy.mjs` to the pull request whose head is the commit CI
ran on.

A CI run dispatched with the workflow token raises no `workflow_run` event, so
the gate cannot follow a maintenance proposal's CI the usual way. Instead, the
last CI job, `Hand the run to the merge gate` (it holds only `actions: write`),
dispatches the gate from main with the run's id. The gate re-reads that run,
waits for it to complete and applies the same policy.

The gate merges a pull request only when all of this holds:

- It is open, not a draft, from this repository, and targets `main`.
- Its author and its head commit's author are `dependabot[bot]`, or
  `github-actions[bot]` for a commit that carries the maintenance trailers.
- The assessed run is a successful, completed CI run of this repository for
  the pull request's current head commit.
- Each of these jobs succeeded exactly once: `Rust quality gate`,
  `Repo checks`, `Security checks`, `Build (Windows x64)`, `WinUI host`,
  `Installer verification / Windows NSIS and MSI smoke` and `CI required`.
  After a partial rerun the gate uses the latest result of each job across the
  attempts of the same run; a newer failure or skip overrides an older
  success.
- Branch protection on `main` requires `CI required`, requires an up-to-date
  branch and is enforced for administrators. When the rule cannot be read, the
  gate refuses to merge.
- GitHub reports the pull request as mergeable and `clean`. A maintenance
  proposal is reported as `unstable` instead, because its held `pull_request`
  CI run counts as a check that is not green. The gate accepts `unstable`
  only when such held CI runs are the sole checks on the head commit that did
  not succeed.
- A Dependabot update is a patch or minor update of every dependency it names
  and changes at most 30 files, each within the dependency-only allowlist:
  lock files, version-only edits of `package.json`, a crate's `Cargo.toml` or
  a `.csproj`, and `uses:` version changes in workflow and action files. A
  major or unrecognized update requires review.
- A maintenance repair is a single commit on current main, matches its
  recipe's allowlist, and is reproduced exactly when the gate reruns the
  recipe from main. A toolchain feed or advisory database that changed in the
  meantime defers the merge until a fresh proposal exists.
- A `release` proposal is never merged by the gate.

The gate merges with a squash merge and passes the expected head commit to
GitHub, which closes the race between verification and merge.

Each time it runs, the gate first closes the maintenance proposals that can
no longer merge, with a comment saying why: those that main has moved past,
and `nuget-locks` repairs whose Dependabot pull request has closed or moved.

## Security repair boundaries

The security recipe uses the same advisory exceptions and denied-warning policy
as the release audit (`scripts/lib/audit-policy.mjs`). It audits current main;
pull request logs supply no package names or commands. Only vulnerable,
actionable-warning or yanked stable crates.io packages are targeted, using
non-yanked updates within the existing major and minor version. Cargo writes
the lock file. Any transitive change must also be a patch update; added or
removed packages and source changes require review. A failed lookup,
resolution, graph check or final audit restores the original lock file and
fails the job. An unchanged, clean main produces no pull request.

The daily schedule also detects an advisory published without a new commit.
A security proposal receives the full CI and installer gates, and the merge
gate replays the recipe from main before merging.

To run the recipe locally (it writes `Cargo.lock` when patches are needed):

```powershell
node scripts/update-security.mjs
```

It needs `cargo-audit` installed and network access to crates.io. The
workflows install `cargo-audit` themselves.

## Repository settings

The automation depends on two repository settings.
`scripts/configure-maintenance.mjs` checks both and can set them. It needs an
authenticated GitHub CLI with repository Administration write access, prints
its plan and changes nothing unless `--apply` is given, and is safe to repeat.

**Pull request creation.** Publishing a proposal needs **Settings → Actions
→ General → Workflow permissions → Allow GitHub Actions to create and approve
pull requests**. It is a repository setting, separate from the workflow's
`pull-requests: write` permission. GitHub exposes creation and approval as one
setting; these workflows do not submit approving reviews. Without it,
publication fails with an error that names the setting and this command:

```powershell
node scripts/configure-maintenance.mjs --plan --permissions-only
node scripts/configure-maintenance.mjs --apply --permissions-only
```

The command keeps the default token permissions and all branch protection,
and verifies the setting after applying it.

**Branch protection.** The merge gate needs the rule described under
[Merging](#merging). Without `--permissions-only` the command also checks it:

```powershell
node scripts/configure-maintenance.mjs --apply
```

Existing protection is validated and kept, including extra checks and review
requirements; protection that does not require `CI required` on an up-to-date
branch, enforced for administrators, stops the command for manual review. When
`main` has no protection, the baseline in `.github/branch-protection.json` is
installed, and only when `CI required` succeeded on the current main commit.
Repository auto-merge settings are not changed; the merge gate performs its
own guarded merge.

To exercise the whole path, run **Bounded maintenance → Run workflow** with
`security` and inspect the audit, the pull request, the dispatched CI, the
installer results and the merge decision.

## Local verification

```powershell
npm run check
npm run test:maintenance
dotnet restore src-winui/SimpleFile.sln --locked-mode
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1
npm run build:plugins
npm run check:winui
npm run build:winui
npm run check:release
npm run check:winui-release-hash
npm run smoke:winui-ui -- -Counts 10000 -MaxWindowWidth 1000
npm run smoke:winui-ui
```

`check:winui` needs the fixture plugin and the bundled plugins that the two
lines before it stage, as the CI job does. `check:release` needs
`cargo-audit`. `-MaxWindowWidth 1000` reproduces a desktop that caps the
window width.

`actionlint` is not part of the repository's tooling and no workflow runs it.
`npm run check:workflows` is the workflow check that CI runs; install
`actionlint` separately to lint workflow syntax as well.

Run the release build and the packaged smokes when changing packaging or
toolchain behavior; the release runbook lists them. `test-results/` and
`dist/` are ignored by Git.
