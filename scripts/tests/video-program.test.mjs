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

// The UI smoke proves the whole chain on Windows (app, file service, video process, Media Foundation) and times
// the new path against the old one, with videos it writes itself.
const read = relative => readFileSync(path.join(root, relative), 'utf8');

test('the UI smoke builds the video program and the tool that writes its test videos', () => {
  const smoke = read('scripts/smoke-winui-ui.ps1');
  // The video program is built optimised, as it ships: the smoke times it against Windows' own optimised code,
  // and an unoptimised build would lose that comparison for a reason no user would ever meet.
  assert.match(smoke, /cargo build --locked --release -p sumafile-video --bin sumafile-video/u);
  assert.match(smoke, /\$video = Join-Path \$root 'target\\release\\sumafile-video\.exe'/u);
  assert.match(smoke, /EnvironmentVariables\['SUMAFILE_VIDEO_HELPER'\] = \$video/u, 'the service is pointed at it');
  assert.match(smoke, /sumafile-video\.exe is missing/u, 'a missing video program fails the smoke');
  assert.match(smoke, /cargo build --locked -p sumafile-video --example write-test-avi/u);
  assert.match(smoke, /write-test-avi\.exe/u);
  assert.match(smoke, /Join-Path \$testRoot 'videos'/u, 'the videos go into the run folder');
  assert.match(smoke, /\$helperExecutables = @\(@\(\$installDir, \$service, \$media, \$video,/u, 'cleanup waits for it');
  assert.match(smoke, /scenario -eq 'video-thumbnails'/u, 'the timings are printed');
});

test('the app runs the video thumbnail scenario in the UI smoke', () => {
  const smoke = read('src-winui/SimpleFile.App/MainWindow.UiSmoke.cs');
  assert.match(smoke, /CheckpointAsync\("video-thumbnails"\)/u);
  const scenario = read('src-winui/SimpleFile.App/MainWindow.UiSmoke.VideoThumbnails.cs');
  assert.match(scenario, /scenario = "video-thumbnails"/u);
  assert.match(scenario, /GenerateVideoThumbnailAsync\(/u, 'the new path');
  assert.match(scenario, /GetMediaStatusAsync\(/u, 'what was decoded is read back');
  assert.match(scenario, /Directory\.EnumerateFiles\(cacheFolder, "\*\.jpg", SearchOption\.AllDirectories\)/u,
    'the thumbnails are looked for on disk: the cache must survive a restart');
});
