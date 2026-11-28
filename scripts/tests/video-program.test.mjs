import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// The video program (sumafile-video) needs Media Foundation, which some Windows editions ship without. A program
// that needs it does not start there. The picture program (sumafile-media) must therefore never come to need it:
// those PCs would lose the picture formats with the video thumbnails.
const root = fileURLToPath(new URL('../..', import.meta.url));
const manifest = crate => readFileSync(path.join(root, 'crates', crate, 'Cargo.toml'), 'utf8');
const dependencyNames = source =>
  [...source.matchAll(/^\s*([A-Za-z0-9_-]+)\s*=/gmu)].map(match => match[1]);

test('the picture program depends on neither the windows crate nor the video program', () => {
  const names = dependencyNames(manifest('sumafile-media'));
  assert.ok(!names.includes('windows'), 'sumafile-media must not depend on the windows crate');
  assert.ok(!names.includes('sumafile-video'), 'sumafile-media must not depend on sumafile-video');
});

test('the crates the picture program is built from do not need the windows crate either', () => {
  for (const crate of ['sumafile-imaging', 'sumafile-media-client']) {
    assert.ok(!dependencyNames(manifest(crate)).includes('windows'), `${crate} must not depend on the windows crate`);
  }
});

test('the video program is a crate of its own and is not published', () => {
  const source = manifest('sumafile-video');
  assert.match(source, /^name = "sumafile-video"$/mu);
  assert.match(source, /^publish = false$/mu);
  assert.ok(dependencyNames(source).includes('sumafile-media'), 'it shares the request loop with sumafile-media');
});
