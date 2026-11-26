import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// scripts\lib\smoke-common.ps1 holds the helpers the release build and the smoke scripts used to repeat.
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const scriptsDir = path.join(repoRoot, 'scripts');
const library = path.join(scriptsDir, 'lib', 'smoke-common.ps1');
const shared = [
  'Write-Step', 'Invoke-Native', 'Start-BackgroundNative', 'Wait-BackgroundNative', 'Stop-BackgroundNative',
  'ConvertTo-NativeArgument', 'Invoke-GitProcess', 'Get-SumaFileDisplayVersion',
  'Find-NewestWinUISetup', 'Resolve-NewInstaller', 'Get-WinUIInstall', 'Test-SameFolder', 'Find-WinUIExecutable',
  'Invoke-Uninstall', 'Wait-PathsExist',
];

test('the library defines every shared helper, and no script defines one again', () => {
  const source = readFileSync(library, 'utf8');
  for (const name of shared) {
    assert.match(source, new RegExp(`^function ${name}\\b`, 'm'), `smoke-common.ps1 defines ${name}`);
  }
  for (const file of readdirSync(scriptsDir).filter(name => name.endsWith('.ps1'))) {
    const script = readFileSync(path.join(scriptsDir, file), 'utf8');
    for (const name of shared) {
      assert.doesNotMatch(script, new RegExp(`^\\s*function ${name}\\b`, 'm'), `${file} has its own ${name}`);
    }
    const uses = shared.filter(name => new RegExp(`(^|[^-\\w])${name}\\b`).test(script.replace(/^\s*#.*$/gm, '')));
    if (uses.length > 0) {
      assert.match(script, /lib\\smoke-common\.ps1/, `${file} uses ${uses.join(', ')} and loads the library`);
    }
  }
});

const pwsh = ['pwsh', 'powershell'].find(name => spawnSync(name, ['-NoProfile', '-Command', 'exit 0']).status === 0);
const git = spawnSync('git', ['--version']).status === 0;

// Runs `body` with the library loaded and returns what it wrote as JSON on its last line.
function run(body) {
  const work = mkdtempSync(path.join(tmpdir(), 'sumafile-smoke-common-'));
  try {
    const harness = path.join(work, 'harness.ps1');
    writeFileSync(harness, [
      "$ErrorActionPreference = 'Stop'",
      'Set-StrictMode -Version 3.0',
      `$work = '${work.replaceAll("'", "''")}'`,
      '$root = $work',
      `. '${library.replaceAll("'", "''")}'`,
      'function Get-Thrown([scriptblock]$Action) { try { & $Action | Out-Null; return $null } catch { return $_.Exception.Message } }',
      body,
      '$result | ConvertTo-Json -Depth 5 -Compress',
    ].join('\n'));
    const result = spawnSync(pwsh, ['-NoProfile', '-NonInteractive', '-File', harness], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr || result.stdout);
    return JSON.parse(result.stdout.trim().split(/\r?\n/).at(-1));
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

test('ConvertTo-NativeArgument quotes as the Windows command line is split', { skip: !pwsh && 'PowerShell is not installed' }, () => {
  const result = run(String.raw`
$result = [ordered]@{
  plain     = ConvertTo-NativeArgument -Value 'plain'
  empty     = ConvertTo-NativeArgument -Value ''
  space     = ConvertTo-NativeArgument -Value 'two words'
  quote     = ConvertTo-NativeArgument -Value 'say "hi"'
  slashes   = ConvertTo-NativeArgument -Value 'a\\"b'
  trailing  = ConvertTo-NativeArgument -Value 'C:\dir with space\'
  untouched = ConvertTo-NativeArgument -Value 'C:\plain\path\'
}`);
  assert.equal(result.plain, 'plain');
  assert.equal(result.empty, '""');
  assert.equal(result.space, '"two words"');
  assert.equal(result.quote, String.raw`"say \"hi\""`);
  // Two backslashes before a quote become four, then the escaped quote.
  assert.equal(result.slashes, String.raw`"a\\\\\"b"`);
  // A trailing backslash is doubled so that it does not escape the closing quote.
  assert.equal(result.trailing, String.raw`"C:\dir with space\\"`);
  assert.equal(result.untouched, String.raw`C:\plain\path` + '\\');
});

test('Test-SameFolder, Wait-PathsExist and Find-WinUIExecutable', { skip: !pwsh && 'PowerShell is not installed' }, () => {
  const result = run(String.raw`
$install = Join-Path $work 'Install Dir'
New-Item -ItemType Directory -Path $install | Out-Null
$entry = [pscustomobject]@{ InstallLocation = '"' + $install.ToUpperInvariant() + '\"' }
$elsewhere = [pscustomobject]@{ InstallLocation = (Join-Path $work 'other') }
$result = [ordered]@{
  same          = Test-SameFolder ('"' + $install.ToUpperInvariant() + '\"') $install
  different     = Test-SameFolder $install (Join-Path $work 'other')
  waitFound     = Get-Thrown { Wait-PathsExist -Paths @($install) -Seconds 1 -Because 'It is there.' }
  waitMissing   = Get-Thrown { Wait-PathsExist -Paths @($install, (Join-Path $work 'never')) -Seconds 1 -Because 'It never comes.' }
  missing       = Find-WinUIExecutable -Installed $entry -InstallDir $install
  missingNeeded = Get-Thrown { Find-WinUIExecutable -Installed $entry -InstallDir $install -Required }
  otherInstall  = Get-Thrown { Find-WinUIExecutable -Installed $elsewhere -InstallDir $install }
}
Set-Content -LiteralPath (Join-Path $install 'SumaFile.exe') -Value 'x'
$result.found = Find-WinUIExecutable -Installed $entry -InstallDir $install -Required
$result.expected = Join-Path $install 'SumaFile.exe'`);
  assert.equal(result.same, true);
  assert.equal(result.different, false);
  assert.equal(result.waitFound, null);
  assert.match(result.waitMissing, /^It never comes\. Missing after 1 s: .*never$/);
  assert.equal(result.missing, null);
  assert.match(result.missingNeeded, /SumaFile\.exe was not found/);
  assert.match(result.otherInstall, /InstallLocation is/);
  assert.equal(result.found, result.expected);
});

test('Get-SumaFileDisplayVersion and Resolve-NewInstaller', { skip: !pwsh && 'PowerShell is not installed' }, () => {
  const result = run(String.raw`
$props = Join-Path $work 'src-winui'
New-Item -ItemType Directory -Path $props | Out-Null
$file = Join-Path $props 'Directory.Build.props'
Set-Content -LiteralPath $file -Value '<Project><PropertyGroup><Version>1.2.3</Version><InformationalVersion>1.2.3-rc.1</InformationalVersion></PropertyGroup></Project>'
$both = Get-SumaFileDisplayVersion -Root $work
Set-Content -LiteralPath $file -Value '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
$numeric = Get-SumaFileDisplayVersion -Root $work
Set-Content -LiteralPath $file -Value '<Project />'
$none = Get-Thrown { Get-SumaFileDisplayVersion -Root $work }

$bundle = Join-Path $work 'dist'
New-Item -ItemType Directory -Path $bundle | Out-Null
$empty = Get-Thrown { Resolve-NewInstaller -BundleDir $bundle }
$older = Join-Path $bundle 'SumaFile_1.0.0_x64-winui-setup.exe'
$newer = Join-Path $bundle 'SumaFile_1.1.0_x64-winui-setup.exe'
Set-Content -LiteralPath $older -Value 'x'
Set-Content -LiteralPath $newer -Value 'x'
Set-Content -LiteralPath (Join-Path $bundle 'SumaFile_9.9.9_x64-winui.msi') -Value 'x'
(Get-Item -LiteralPath $older).LastWriteTime = (Get-Date).AddHours(-2)
$result = [ordered]@{
  both = $both; numeric = $numeric; none = $none; empty = $empty
  newest = Resolve-NewInstaller -BundleDir $bundle
  explicit = Resolve-NewInstaller -Explicit $older -BundleDir $bundle
  newer = $newer; older = $older
}`);
  assert.equal(result.both, '1.2.3-rc.1');
  assert.equal(result.numeric, '1.2.3');
  assert.match(result.none, /Could not read InformationalVersion or Version/);
  assert.match(result.empty, /No WinUI NSIS installer found/);
  assert.equal(result.newest, result.newer);
  assert.equal(result.explicit, result.older);
});

test('Invoke-GitProcess returns output, sets and removes variables, and reports a failure', { skip: (!pwsh || !git) && 'PowerShell or git is not installed' }, () => {
  const result = run(String.raw`
$config = Join-Path $work 'gitconfig'
Set-Content -LiteralPath $config -Value "[user]\`n\`tname = Smoke Common"
$gitExe = (Get-Command git).Source
$environment = @{ GIT_CONFIG_GLOBAL = $config; GIT_CONFIG_NOSYSTEM = '1'; GIT_CEILING_DIRECTORIES = $work; GIT_DIR = $null }
$env:GIT_DIR = Join-Path $work 'not-a-repository'
try {
  $result = [ordered]@{
    name    = (Invoke-GitProcess -GitExe $gitExe -WorkingDirectory $work -Arguments @('config', '--global', '--get', 'user.name') -Environment $environment).Trim()
    spaced  = (Invoke-GitProcess -GitExe $gitExe -WorkingDirectory $work -Arguments @('-c', 'smoke.value=two words', 'config', '--get', 'smoke.value') -Environment $environment).Trim()
    failure = Get-Thrown { Invoke-GitProcess -GitExe $gitExe -WorkingDirectory $work -Arguments @('rev-parse', '--show-toplevel') -Environment $environment }
  }
} finally { Remove-Item Env:GIT_DIR }`.replaceAll('\\`', '`'));
  assert.equal(result.name, 'Smoke Common');
  // An argument with a space reaches git as one argument.
  assert.equal(result.spaced, 'two words');
  assert.match(result.failure, /^'git rev-parse --show-toplevel' failed in .* \(exit \d+\): .*not a git repository/s);
});

// The release build makes the setup in the background while it makes the ZIP and the MSI.
// The programs it starts here are cmd.exe commands, so this one needs Windows as well as PowerShell: the
// repo checks run on Ubuntu, which has PowerShell and no cmd.exe.
const backgroundNativeSkip = (!pwsh && 'PowerShell is not installed')
  || (process.platform !== 'win32' && 'starts cmd.exe, which only Windows has');
test('Start-BackgroundNative returns at once; Wait-BackgroundNative prints the output and reports a failure', { skip: backgroundNativeSkip }, () => {
  const result = run(String.raw`
$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$started = [System.Diagnostics.Stopwatch]::StartNew()
$slow = Start-BackgroundNative -Title 'Slow step' -FilePath $cmd -ArgumentList @('/c', 'ping -n 3 127.0.0.1 >nul & echo slow step done') 6>$null
$returnedAfter = $started.Elapsed.TotalMilliseconds
$stillRunning = -not $slow.Process.HasExited
$printed = (Wait-BackgroundNative -Job $slow 6>&1 | Out-String)
$waited = $started.Elapsed.TotalMilliseconds

$failing = Start-BackgroundNative -Title 'Failing step' -FilePath $cmd -ArgumentList @('/c', 'echo it went wrong 1>&2 & exit 3') 6>$null
$failurePrinted = ''
$failure = Get-Thrown { $script:failurePrinted = (Wait-BackgroundNative -Job $failing 6>&1 | Out-String) }

$abandoned = Start-BackgroundNative -Title 'Abandoned step' -FilePath $cmd -ArgumentList @('/c', 'ping -n 30 127.0.0.1 >nul') 6>$null
$abandonedId = $abandoned.Process.Id
Stop-BackgroundNative -Job $abandoned
Start-Sleep -Milliseconds 300
$result = [ordered]@{
  returnedAfter = $returnedAfter
  stillRunning  = $stillRunning
  printed       = $printed
  waited        = $waited
  failure       = $failure
  stopped       = -not (Get-Process -Id $abandonedId -ErrorAction SilentlyContinue)
  stopNothing   = Get-Thrown { Stop-BackgroundNative -Job $null }
  stopFinished  = Get-Thrown { Stop-BackgroundNative -Job $slow }
}`);
  assert.ok(result.returnedAfter < 1500, `returned after ${result.returnedAfter} ms`);
  assert.equal(result.stillRunning, true);
  assert.ok(result.waited >= 1500, `the wait covered the program's run: ${result.waited} ms`);
  assert.match(result.printed, /Slow step: finished after \d+ s/);
  assert.match(result.printed, /slow step done/);
  assert.match(result.failure, /Command failed with exit code 3: .*cmd\.exe/);
  assert.equal(result.stopped, true);
  assert.equal(result.stopNothing, null);
  assert.equal(result.stopFinished, null);
});
