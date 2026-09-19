import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { API, isNewerVersion } from '../api.js';

describe('isNewerVersion', () => {
  it('compares version numbers, not text', () => {
    assert.equal(isNewerVersion('1.4.1', '1.4.0'), true);
    assert.equal(isNewerVersion('v1.10.0', '1.9.9'), true);
    assert.equal(isNewerVersion('2.0', '1.9.9'), true);
    assert.equal(isNewerVersion('1.4.0', '1.4.0'), false);
    assert.equal(isNewerVersion('1.3.9', '1.4.0'), false);
    assert.equal(isNewerVersion('1.4.0', '1.4.1'), false);
  });

  it('treats anything unreadable as not newer', () => {
    assert.equal(isNewerVersion('', '1.4.0'), false);
    assert.equal(isNewerVersion('unknown', '1.4.0'), false);
  });
});

describe('API', () => {
  it('points at AdBlocker on this PC only', () => {
    assert.equal(API, 'http://127.0.0.1:45353/v1');
  });
});
