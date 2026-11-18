import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { runtimeLineViolations } from '../lib/dotnet-packages.mjs';

const project = (framework, packages) => `<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>${framework}</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n${packages.map(([name, version]) => `    <PackageReference Include="${name}" Version="${version}" />`).join('\n')}\n  </ItemGroup>\n</Project>\n`;

function fixture(files) {
  const root = mkdtempSync(join(tmpdir(), 'sumafile-packages-'));
  for (const [path, content] of Object.entries(files)) {
    mkdirSync(join(root, path, '..'), { recursive: true });
    writeFileSync(join(root, path), content);
  }
  return root;
}

// System.* and Microsoft.Extensions.* ship with each .NET: their next major is for the next .NET, and mixing it
// into an app on the current one leaves two runtime versions in the build (MSB3277 conflicts).
test('a runtime-line package from the next .NET major is a violation', () => {
  const root = fixture({ 'src-winui/App/App.csproj': project('net10.0-windows10.0.19041.0', [['System.Drawing.Common', '11.0.0'], ['Markdig', '1.6.0']]) });
  try {
    assert.deepEqual(runtimeLineViolations(root), [
      { project: 'src-winui/App/App.csproj', package: 'System.Drawing.Common', version: '11.0.0', framework: 'net10.0-windows10.0.19041.0' },
    ]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
test('packages on the app\'s .NET line or an older one, and packages outside the runtime families, pass', () => {
  const root = fixture({
    'src-winui/Core/Core.csproj': project('net10.0-windows', [['System.Data.OleDb', '9.0.23'], ['Microsoft.Extensions.DependencyInjection', '10.0.15'], ['CommunityToolkit.Mvvm', '8.4.2']]),
    'plugins/git/ui/Git/Git.csproj': project('net10.0-windows10.0.19041.0', [['Microsoft.WindowsAppSDK', '2.4.0']]),
  });
  try { assert.deepEqual(runtimeLineViolations(root), []); } finally { rmSync(root, { recursive: true, force: true }); }
});
test('the target framework can come from Directory.Build.props', () => {
  const root = fixture({
    'src-winui/Directory.Build.props': '<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n  </PropertyGroup>\n</Project>\n',
    'src-winui/Core/Core.csproj': '<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n    <PackageReference Include="System.Text.Json" Version="11.0.0" />\n  </ItemGroup>\n</Project>\n',
  });
  try { assert.equal(runtimeLineViolations(root).length, 1); } finally { rmSync(root, { recursive: true, force: true }); }
});
test('the repository has no runtime-line package from the next .NET', () => {
  assert.deepEqual(runtimeLineViolations(resolve(import.meta.dirname, '../..')), []);
});
