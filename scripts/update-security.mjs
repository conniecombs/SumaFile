import { updateSecurity } from './lib/security-updates.mjs';

try {
  const updates = await updateSecurity();
  console.log(updates.length ? updates.join('\n') : 'Release audit passed; no security update needed.');
} catch (error) {
  console.error(`Security maintenance requires review: ${error.message}`);
  process.exitCode = 1;
}
