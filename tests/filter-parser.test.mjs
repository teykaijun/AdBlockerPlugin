import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { ALLOW_RULE_ID_BASE, RULE_PRIORITY } from '../src/shared/defaults.js';
import { compileNetworkRules, parseFilterList } from '../src/shared/filter-parser.js';

const one = (line) => {
  const { network, errors } = parseFilterList(line);
  assert.deepEqual(errors, [], `unexpected errors for ${line}`);
  assert.equal(network.length, 1);
  return network[0].rule;
};

const errorOf = (line) => {
  const { errors } = parseFilterList(line);
  assert.equal(errors.length, 1, `expected an error for ${line}`);
  return errors[0].message;
};

describe('network filters', () => {
  it('turns ||domain^ into requestDomains', () => {
    assert.deepEqual(one('||Ads.Example.com^'), {
      priority: RULE_PRIORITY.block,
      action: { type: 'block' },
      condition: { requestDomains: ['ads.example.com'] },
    });
  });

  it('accepts hosts-file lines and bare hostnames', () => {
    assert.deepEqual(one('0.0.0.0 tracker.example.net').condition, { requestDomains: ['tracker.example.net'] });
    assert.deepEqual(one('127.0.0.1 tracker.example.net').condition, { requestDomains: ['tracker.example.net'] });
    assert.deepEqual(one('tracker.example.net').condition, { requestDomains: ['tracker.example.net'] });
  });

  it('does not mistake file names for hostnames', () => {
    assert.deepEqual(one('ads.js').condition, { urlFilter: 'ads.js' });
  });

  it('refuses to block localhost', () => {
    assert.match(errorOf('0.0.0.0 localhost'), /localhost/);
  });

  it('keeps URL patterns and parses options', () => {
    assert.deepEqual(one('||example.com/ads/*$script,image,third-party').condition, {
      urlFilter: '||example.com/ads/*',
      resourceTypes: ['image', 'script'],
      domainType: 'thirdParty',
    });
  });

  it('maps party aliases', () => {
    assert.equal(one('/x/$1p').condition.domainType, 'firstParty');
    assert.equal(one('/x/*$3p').condition.domainType, 'thirdParty');
    assert.equal(one('/x/*$~third-party').condition.domainType, 'firstParty');
  });

  it('parses $domain with exclusions', () => {
    assert.deepEqual(one('/banner/*$domain=news.com|~sports.news.com').condition, {
      urlFilter: '/banner/*',
      initiatorDomains: ['news.com'],
      excludedInitiatorDomains: ['sports.news.com'],
    });
  });

  it('allows a pattern-less filter when it is scoped by domain', () => {
    assert.deepEqual(one('*$image,domain=example.com').condition, {
      initiatorDomains: ['example.com'],
      resourceTypes: ['image'],
    });
  });

  it('parses $to, $denyallow, $method and $match-case', () => {
    assert.deepEqual(one('/pixel$to=a.com|~b.a.com').condition, {
      urlFilter: '/pixel',
      requestDomains: ['a.com'],
      excludedRequestDomains: ['b.a.com'],
    });
    assert.deepEqual(one('*$script,denyallow=cdn.com,domain=site.com').condition, {
      excludedRequestDomains: ['cdn.com'],
      initiatorDomains: ['site.com'],
      resourceTypes: ['script'],
    });
    assert.deepEqual(one('/collect$method=post|~get').condition, {
      urlFilter: '/collect',
      requestMethods: ['post'],
      excludedRequestMethods: ['get'],
    });
    assert.equal(one('/AdBanner$match-case').condition.isUrlFilterCaseSensitive, true);
  });

  it('uses excludedResourceTypes for negated types', () => {
    assert.deepEqual(one('||example.com/x$~image,~font').condition, {
      urlFilter: '||example.com/x',
      excludedResourceTypes: ['font', 'image'],
    });
  });

  it('parses regular expressions, including a trailing option block', () => {
    assert.deepEqual(one('/ads\\d+\\.js/$script').condition, { regexFilter: 'ads\\d+\\.js', resourceTypes: ['script'] });
    assert.deepEqual(one('/banner[0-9]+/').condition, { regexFilter: 'banner[0-9]+' });
    assert.match(errorOf('/ads(/'), /regular expression/);
  });

  it('builds allow and allowAllRequests rules', () => {
    assert.deepEqual(one('@@||cdn.example.com/player.js'), {
      priority: RULE_PRIORITY.allow,
      action: { type: 'allow' },
      condition: { urlFilter: '||cdn.example.com/player.js' },
    });
    assert.deepEqual(one('@@||example.com^$document'), {
      priority: RULE_PRIORITY.allow,
      action: { type: 'allowAllRequests' },
      condition: { requestDomains: ['example.com'], resourceTypes: ['main_frame', 'sub_frame'] },
    });
    assert.match(errorOf('@@||example.com^$document,script'), /cannot be combined/);
  });

  it('raises priority for $important', () => {
    assert.equal(one('||ads.com^$important').priority, RULE_PRIORITY.importantBlock);
    assert.equal(one('@@||ads.com^$important').priority, RULE_PRIORITY.importantAllow);
  });

  it('strips a leading ||* which Chrome rejects', () => {
    assert.equal(one('||*/ads/banner.gif').condition.urlFilter, '*/ads/banner.gif');
  });

  it('rejects filters it cannot honour', () => {
    assert.match(errorOf('||example.com^$redirect=noop.js'), /Unsupported option "\$redirect"/);
    assert.match(errorOf('||example.com^$popup'), /Unsupported option/);
    assert.match(errorOf('$script'), /too broad/);
    assert.match(errorOf('*'), /too broad/);
    assert.match(errorOf('||example.com^$domain=example.*'), /Wildcard TLD/);
    assert.match(errorOf('||exämple.com/ads'), /ASCII/);
    assert.match(errorOf('/x$method=fetch'), /Unknown HTTP method/);
  });

  it('reports 1-based line numbers', () => {
    const { errors } = parseFilterList('! comment\n||ok.com^\n$script\n');
    assert.deepEqual(errors, [{ line: 3, text: '$script', message: 'Filter is too broad: it would block every request' }]);
  });
});

describe('comments', () => {
  it('ignores comments, headers and blank lines', () => {
    const { network, cosmetic, errors } = parseFilterList('[Adblock Plus 2.0]\n! comment\n# hosts comment\n#another\n\n   \n');
    assert.equal(network.length, 0);
    assert.deepEqual(cosmetic, { generic: [], byHost: {}, exceptions: {} });
    assert.deepEqual(errors, []);
  });
});

describe('cosmetic filters', () => {
  it('collects generic, site-specific and exception selectors', () => {
    const { cosmetic, errors } = parseFilterList(
      [
        '##.ad-banner',
        '###sidebar-ad',
        'example.com,Other.org##.promo',
        'example.com#@#.ad-banner',
        '~safe.example.com##.sponsored',
        'news.com,~blog.news.com##.teaser-ad',
      ].join('\n'),
    );
    assert.deepEqual(errors, []);
    assert.deepEqual(cosmetic, {
      generic: ['.ad-banner', '#sidebar-ad', '.sponsored'],
      byHost: { 'example.com': ['.promo'], 'other.org': ['.promo'], 'news.com': ['.teaser-ad'] },
      exceptions: {
        'example.com': ['.ad-banner'],
        'safe.example.com': ['.sponsored'],
        'blog.news.com': ['.teaser-ad'],
      },
    });
  });

  it('converts :-abp-has to :has', () => {
    assert.deepEqual(parseFilterList('example.com##div:-abp-has(> .ad)').cosmetic.byHost, {
      'example.com': ['div:has(> .ad)'],
    });
  });

  it('rejects unsupported and unsafe cosmetic filters', () => {
    assert.match(errorOf('example.com##+js(set-constant, x, 1)'), /Scriptlet/);
    assert.match(errorOf('example.com#?#div:has-text(Sponsored)'), /not supported/);
    assert.match(errorOf('example.com##div:has-text(Sponsored)'), /Procedural/);
    assert.match(errorOf('example.com#$#abort-on-property-read x'), /not supported/);
    assert.match(errorOf('##a{} body{display:none'), /not allowed/);
    assert.match(errorOf('##div /* comment'), /not allowed/);
    assert.match(errorOf('#@#.ad'), /need at least one domain/);
  });
});

describe('compileNetworkRules', () => {
  it('merges plain domain rules that share options and assigns ids', () => {
    const { network } = parseFilterList(
      [
        '||b.com^',
        '||a.com^',
        '||c.com^$third-party',
        '||d.com^$third-party',
        '/ads/*',
        '@@||ok.com^',
        '@@||fine.com^',
        '@@/ads/allowed.js',
      ].join('\n'),
    );
    const compiled = compileNetworkRules(network);
    assert.deepEqual(
      compiled.map((c) => c.rule),
      [
        { id: 1, priority: 1, action: { type: 'block' }, condition: { requestDomains: ['a.com', 'b.com'] } },
        {
          id: 2,
          priority: 1,
          action: { type: 'block' },
          condition: { domainType: 'thirdParty', requestDomains: ['c.com', 'd.com'] },
        },
        {
          id: ALLOW_RULE_ID_BASE,
          priority: 2,
          action: { type: 'allow' },
          condition: { requestDomains: ['fine.com', 'ok.com'] },
        },
        { id: 3, priority: 1, action: { type: 'block' }, condition: { urlFilter: '/ads/*' } },
        { id: ALLOW_RULE_ID_BASE + 1, priority: 2, action: { type: 'allow' }, condition: { urlFilter: '/ads/allowed.js' } },
      ],
    );
    assert.deepEqual(compiled[0].lines, [1, 2]);
  });

  it('honours custom id starts', () => {
    const { network } = parseFilterList('||a.com^\n@@||b.com^');
    const ids = compileNetworkRules(network, { blockIdStart: 10, allowIdStart: 500 }).map((c) => c.rule.id);
    assert.deepEqual(ids, [10, 500]);
  });
});
