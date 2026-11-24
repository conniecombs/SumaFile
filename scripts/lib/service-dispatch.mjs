// Reads which JSON-RPC methods the file service dispatches. Shared by the checks that compare
// that set with the IPC schema and with the WinUI parity record, so the two cannot come to
// read the dispatcher differently.

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export const dispatchFiles = ['mod.rs', 'handlers.rs', 'async_ops.rs', 'params.rs'];

/** The dispatcher's source files, joined. */
export function readDispatchSource(repoRoot) {
  return dispatchFiles
    .map((file) => readFileSync(join(repoRoot, 'crates/simplefile-service/src/dispatch', file), 'utf8'))
    .join('\n');
}

/** `METHOD_*` constant name to wire name, from the generated protocol source. */
export function rustMethodConstants(source) {
  return new Map(
    [...source.matchAll(/pub const (METHOD_[A-Z0-9_]+):\s*&str\s*=\s*"([a-z0-9_]+)"/g)].map(
      (match) => [match[1], match[2]],
    ),
  );
}

/**
 * The wire names of the methods the dispatcher has an arm for, in source order: arms written
 * as a string literal and arms written as a `METHOD_*` constant. Throws when an arm names a
 * constant the protocol source does not define, or when no arm is found at all.
 */
export function activeServiceCommands(source, rustConstants) {
  const commands = [...source.matchAll(/^\s*"([a-z0-9_]+)"\s*=>/gm)].map((match) => match[1]);
  for (const match of source.matchAll(/^\s*(METHOD_[A-Z0-9_]+)\s*=>/gm)) {
    const method = rustConstants.get(match[1]);
    if (!method) {
      throw new Error(`Unknown generated method constant in service dispatcher: ${match[1]}`);
    }
    commands.push(method);
  }
  if (commands.length === 0) {
    throw new Error('Could not find JSON-RPC method arms in crates/simplefile-service/src/dispatch');
  }
  return commands;
}
