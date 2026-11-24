import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  activeServiceCommands,
  readDispatchSource,
  rustMethodConstants,
} from '../lib/service-dispatch.mjs';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

const constants = rustMethodConstants(`
pub const METHOD_LIST_DIRECTORY: &str = "list_directory";
pub const METHOD_PING: &str = "ping";
pub const EVENT_FILE_CHANGE: &str = "file-change";
`);

test('method constants map their names to wire names and leave other constants out', () => {
  assert.deepEqual([...constants], [
    ['METHOD_LIST_DIRECTORY', 'list_directory'],
    ['METHOD_PING', 'ping'],
  ]);
});

test('arms written as literals and as constants are both commands', () => {
  const source = `
    match request.method.as_str() {
        "handshake" => handshake(request),
        METHOD_LIST_DIRECTORY => list(request),
        METHOD_PING => ping(request),
        other => unknown(other),
    }
    let label = match kind { Kind::A => "not_a_method" };
  `;
  assert.deepEqual(activeServiceCommands(source, constants), ['handshake', 'list_directory', 'ping']);
});

test('an arm for a constant the protocol does not define is an error', () => {
  assert.throws(
    () => activeServiceCommands('    METHOD_GONE => gone(request),', constants),
    /Unknown generated method constant in service dispatcher: METHOD_GONE/,
  );
});

test('a dispatcher with no method arm is an error', () => {
  assert.throws(() => activeServiceCommands('fn nothing() {}', constants), /Could not find JSON-RPC method arms/);
});

test('the repository dispatcher is read and names only methods the protocol defines', () => {
  const source = readDispatchSource(repoRoot);
  assert.match(source, /METHOD_LIST_DIRECTORY\s*=>/);
  const protocol = readFileSync(resolve(repoRoot, 'crates/simplefile-ipc/src/protocol_generated.rs'), 'utf8');
  const commands = activeServiceCommands(source, rustMethodConstants(protocol));
  assert.equal(new Set(commands).size, commands.length, 'no method has two arms');
});
