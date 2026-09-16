import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

import { FILTER_LISTS, isBlockingRuleId } from '../src/shared/defaults.js';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const manifest = JSON.parse(readFileSync(path.join(ROOT, 'manifest.json'), 'utf8'));

const CONDITION_KEYS = new Set([
  'urlFilter',
  'regexFilter',
  'isUrlFilterCaseSensitive',
  'initiatorDomains',
  'excludedInitiatorDomains',
  'requestDomains',
  'excludedRequestDomains',
  'resourceTypes',
  'excludedResourceTypes',
  'requestMethods',
  'excludedRequestMethods',
  'domainType',
]);
const RESOURCE_TYPES = new Set([
  'main_frame',
  'sub_frame',
  'stylesheet',
  'script',
  'image',
  'font',
  'object',
  'xmlhttprequest',
  'ping',
  'csp_report',
  'media',
  'websocket',
  'webtransport',
  'webbundle',
  'other',
]);

describe('build output', () => {
  it('is up to date with filters/*.txt', () => {
    execFileSync(process.execPath, [path.join(ROOT, 'scripts/build.mjs'), '--check'], { stdio: 'pipe' });
  });

  it('references files that exist', () => {
    const files = [
      manifest.background.service_worker,
      manifest.action.default_popup,
      manifest.options_ui.page,
      ...Object.values(manifest.icons),
      ...Object.values(manifest.action.default_icon),
      ...manifest.content_scripts.flatMap((c) => c.js),
      ...manifest.declarative_net_request.rule_resources.map((r) => r.path),
      'icons/icon-off-16.png',
      'icons/icon-off-32.png',
      'src/content/picker.js',
      'generated/list-stats.json',
    ];
    for (const file of files) assert.ok(existsSync(path.join(ROOT, file)), `missing ${file}`);
  });

  it('declares one ruleset per filter list', () => {
    assert.deepEqual(
      manifest.declarative_net_request.rule_resources.map((r) => r.id),
      FILTER_LISTS.map((l) => l.id),
    );
  });

  for (const resource of manifest.declarative_net_request.rule_resources) {
    it(`produces valid DNR rules for "${resource.id}"`, () => {
      const rules = JSON.parse(readFileSync(path.join(ROOT, resource.path), 'utf8'));
      const ids = new Set();
      for (const rule of rules) {
        assert.deepEqual(Object.keys(rule).sort(), ['action', 'condition', 'id', 'priority']);
        assert.ok(Number.isInteger(rule.id) && rule.id > 0);
        assert.ok(!ids.has(rule.id), `duplicate id ${rule.id}`);
        ids.add(rule.id);
        assert.equal(isBlockingRuleId(rule.id), rule.action.type === 'block', `id ${rule.id} is in the wrong range`);
        assert.ok(['block', 'allow', 'allowAllRequests'].includes(rule.action.type));
        for (const key of Object.keys(rule.condition)) assert.ok(CONDITION_KEYS.has(key), `unknown key ${key}`);
        for (const t of [...(rule.condition.resourceTypes ?? []), ...(rule.condition.excludedResourceTypes ?? [])]) {
          assert.ok(RESOURCE_TYPES.has(t), `unknown resource type ${t}`);
        }
        assert.ok(!(rule.condition.urlFilter && rule.condition.regexFilter));
      }
    });
  }
});
