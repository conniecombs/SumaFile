import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';

// System.* and Microsoft.Extensions.* ship with each .NET. Their next major is for the next .NET: mixed into an app on
// the current one it leaves two runtime versions in the build (MSB3277 conflicts). Dependabot is told not to propose
// them (.github/dependabot.yml); this check holds the project files to the same rule.
const families = /^(?:System\.|Microsoft\.Extensions\.)/;
const skipped = new Set(['.git', 'node_modules', 'bin', 'obj', 'target', 'dist', 'test-results']);

function* projects(root, directory = root) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory()) { if (!skipped.has(entry.name)) yield* projects(root, join(directory, entry.name)); }
    else if (entry.name.endsWith('.csproj')) yield join(directory, entry.name);
  }
}

function targetFramework(root, project) {
  for (let directory = dirname(project); ; directory = dirname(directory)) {
    const candidates = directory === dirname(project) ? [project, join(directory, 'Directory.Build.props')] : [join(directory, 'Directory.Build.props')];
    for (const file of candidates) {
      if (!existsSync(file)) continue;
      const match = /<TargetFramework>([^<]+)<\/TargetFramework>/.exec(readFileSync(file, 'utf8'));
      if (match) return match[1].trim();
    }
    if (directory === root || dirname(directory) === directory) return null;
  }
}

/** Every runtime-line package reference whose major version is above the project's .NET major. */
export function runtimeLineViolations(root) {
  const violations = [];
  for (const project of projects(root)) {
    const framework = targetFramework(root, project);
    const frameworkMajor = Number(/^net(\d+)\./.exec(framework ?? '')?.[1]);
    if (!Number.isInteger(frameworkMajor)) continue;
    const source = readFileSync(project, 'utf8');
    for (const [, name, version] of source.matchAll(/<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"/g)) {
      if (!families.test(name)) continue;
      if (Number(version.split('.')[0]) > frameworkMajor) {
        violations.push({ project: relative(root, project).split(sep).join('/'), package: name, version, framework });
      }
    }
  }
  return violations;
}
