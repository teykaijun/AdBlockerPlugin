import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import { API } from '../api.js';

/** A file in this repository, by its path from the top. */
const read = (path) => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');

/** The first group `pattern` captures in that file. */
function find(path, pattern) {
  const match = read(path).match(pattern);
  assert.ok(match, `${pattern} isn't in ${path}`);
  return match[1];
}

const manifest = JSON.parse(read('browser-extension/manifest.json'));

describe('the companion and the apps', () => {
  // The popup asks people to update the companion whenever AdBlocker for Windows is newer, so a
  // missed version bump would ask everyone who installed this release.
  it('have the same version', () => {
    for (const [path, pattern] of [
      ['windows/Directory.Build.props', /<Version>([^<]+)<\/Version>/],
      ['android/app/build.gradle.kts', /versionName\s*=\s*"([^"]+)"/],
    ]) {
      const version = find(path, pattern);
      assert.equal(version, manifest.version, `${path} says ${version}, manifest.json says ${manifest.version}`);
    }
  });

  it('use the same port, and the companion may only reach that one', () => {
    const port = find('windows/src/AdBlocker/Core/LocalApi.cs', /DefaultPort\s*=\s*(\d+);/);
    assert.equal(new URL(API).port, port);
    assert.deepEqual(manifest.host_permissions, [`http://127.0.0.1:${port}/*`]);
  });
});
