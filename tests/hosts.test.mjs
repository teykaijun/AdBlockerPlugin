import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { hostMatches, hostSuffixes, isAllowlisted, isValidHostname, normalizeHost, parseHostInput } from '../src/shared/hosts.js';

describe('hosts', () => {
  it('normalizes hostnames', () => {
    assert.equal(normalizeHost('WWW.Example.COM.'), 'example.com');
    assert.equal(normalizeHost(' sub.example.com '), 'sub.example.com');
  });

  it('validates hostnames', () => {
    assert.ok(isValidHostname('example.com'));
    assert.ok(isValidHostname('xn--bcher-kva.example'));
    assert.ok(isValidHostname('localhost'));
    assert.ok(!isValidHostname('-bad.com'));
    assert.ok(!isValidHostname('bad-.com'));
    assert.ok(!isValidHostname('exa mple.com'));
    assert.ok(!isValidHostname(''));
  });

  it('parses whatever users paste into the allowlist box', () => {
    assert.equal(parseHostInput('https://www.Example.com/some/path?q=1'), 'example.com');
    assert.equal(parseHostInput('  news.example.org  '), 'news.example.org');
    assert.equal(parseHostInput('example.com:8080'), 'example.com');
    assert.equal(parseHostInput('bücher.example'), 'xn--bcher-kva.example');
    assert.equal(parseHostInput(''), null);
    assert.equal(parseHostInput('not a host'), null);
  });

  it('matches subdomains but not lookalikes', () => {
    assert.ok(hostMatches('example.com', 'example.com'));
    assert.ok(hostMatches('a.b.example.com', 'example.com'));
    assert.ok(!hostMatches('badexample.com', 'example.com'));
    assert.ok(!hostMatches('example.com', 'a.example.com'));
    assert.ok(isAllowlisted('www.example.com', ['other.org', 'example.com']));
    assert.ok(!isAllowlisted('', ['example.com']));
  });

  it('lists host suffixes', () => {
    assert.deepEqual(hostSuffixes('a.b.example.com'), ['a.b.example.com', 'b.example.com', 'example.com', 'com']);
  });
});
