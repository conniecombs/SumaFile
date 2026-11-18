import { resolve } from 'node:path';
import { runtimeLineViolations } from './lib/dotnet-packages.mjs';

const violations = runtimeLineViolations(resolve(import.meta.dirname, '..'));
if (violations.length) {
  for (const { project, package: name, version, framework } of violations) {
    console.error(`${project}: ${name} ${version} is the next .NET's line; the project targets ${framework}.`);
  }
  console.error('Keep System.* and Microsoft.Extensions.* on the .NET line the app targets.');
  process.exit(1);
}
console.log('Runtime-line NuGet packages match the targeted .NET.');
