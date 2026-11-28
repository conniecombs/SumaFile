import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// `.gitignore` ignores every `bin/` folder, because that is where .NET puts build output. Cargo's standard place for
// a crate's extra programs is `src/bin/`, so a Rust program written there was ignored and never committed: it built
// and passed on the machine that wrote it and failed on CI with "can't find bin". These tests keep that from
// happening again.
const root = fileURLToPath(new URL('../..', import.meta.url));
const git = (...args) => spawnSync('git', args, { cwd: root, encoding: 'utf8' });
const inWorkTree = git('rev-parse', '--is-inside-work-tree').stdout?.trim() === 'true';

// Whether the ignore rules cover `path`, whether or not the file exists or is tracked.
const ignored = path => git('check-ignore', '--no-index', '-q', path).status === 0;

test('a Rust program in src/bin is not ignored by git', { skip: !inWorkTree }, () => {
  for (const path of [
    'crates/any-crate/src/bin/any-program.rs',
    'crates/any-crate/src/bin/any-program/main.rs',
    'plugins/any/backend/src/bin/any-program.rs',
  ]) {
    assert.equal(ignored(path), false, `${path} would be ignored: .gitignore must not cover src/bin/`);
  }
});

test('.NET build output is still ignored', { skip: !inWorkTree }, () => {
  for (const path of [
    'src-winui/SimpleFile.Core/bin/Debug/net10.0/SimpleFile.Core.dll',
    'plugins/git/ui/SumaFile.Git/bin/Release/SumaFile.Git.dll',
    'src-winui/SimpleFile.App/obj/project.assets.json',
  ]) {
    assert.equal(ignored(path), true, `${path} must stay ignored`);
  }
});

test('no source file in the working tree is hidden by an ignore rule', { skip: !inWorkTree }, () => {
  // Files that exist, are not tracked and are ignored. On a fresh checkout there are none; on a developer's machine
  // this is the list a commit silently leaves out.
  const listed = git('ls-files', '--others', '--ignored', '--exclude-standard', '--', 'crates', 'plugins', 'ipc');
  assert.equal(listed.status, 0, listed.stderr);
  const hidden = listed.stdout
    .split('\n')
    .filter(Boolean)
    .filter(path => /\.(rs|toml|json|cs|csproj|xaml|mjs)$/.test(path))
    .filter(path => !/(^|\/)(target|obj|node_modules)\//.test(path))
    .filter(path => !/\/bin\/(Debug|Release)\//.test(path));
  assert.deepEqual(hidden, [], 'these source files exist but git ignores them, so they would never be committed');
});
