import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { parseWorkflow, validateWorkflows } from './lib/workflow-policy.mjs';

const root = resolve(import.meta.dirname, '..');
try {
  const workflows = Object.fromEntries(readdirSync(resolve(root, '.github/workflows'))
    .filter(name => /\.ya?ml$/.test(name))
    .map(name => [name, parseWorkflow(readFileSync(resolve(root, '.github/workflows', name), 'utf8'))]));
  const dependabot = parseWorkflow(readFileSync(resolve(root, '.github/dependabot.yml'), 'utf8'));
  const findings = validateWorkflows(workflows, dependabot);
  if (findings.length) throw new Error(findings.join('\n'));
  console.log('Workflow triggers, job gates, permissions, actions and packaging contracts are valid.');
} catch (error) {
  console.error(`GitHub workflow check failed: ${error.message}`);
  process.exitCode = 1;
}
