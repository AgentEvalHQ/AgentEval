// Render the production components with a populated query cache. No network or
// browser service is required; only the transport module is replaced.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { MemoryRouter, Routes, Route } = require('react-router');
const { QueryClient, QueryClientProvider } = require('@tanstack/react-query');
const root = path.resolve(__dirname, '../src');
const modules = new Map();
function load(file) {
  file = path.resolve(file);
  if (!path.extname(file)) file += fs.existsSync(file + '.tsx') ? '.tsx' : '.ts';
  if (modules.has(file)) return modules.get(file).exports;
  if (file === path.join(root, 'lib/graphql-client.ts'))
    return { gqlRequest: () => { throw new Error('Unexpected network request'); } };
  const module = { exports: {} };
  modules.set(file, module);
  const code = ts.transpileModule(fs.readFileSync(file, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  const localRequire = name => name.startsWith('@/') ? load(path.join(root, name.slice(2)))
    : name.startsWith('.') ? load(path.resolve(path.dirname(file), name)) : require(name);
  vm.runInThisContext(`(function(require,module,exports){${code}\n})`, { filename: file })(localRequire, module, module.exports);
  return module.exports;
}
const { queryKeys } = load(path.join(root, 'lib/keys'));
const { RunDetailPage } = load(path.join(root, 'pages/RunDetailPage'));
const { TraceWaterfallPage } = load(path.join(root, 'pages/TraceWaterfallPage'));
const { restUrls, fetchTrace } = load(path.join(root, 'lib/rest-client'));
const { RunsListPage } = load(path.join(root, 'pages/RunsListPage'));
const { AppShell } = load(path.join(root, 'components/AppShell'));
const { SubjectDetailPage } = load(path.join(root, 'pages/SubjectDetailPage'));
const { SubjectCard } = load(path.join(root, 'components/SubjectCard'));
const { ManifestHashBadge } = load(path.join(root, 'components/ManifestHashBadge'));
const h = React.createElement;
function render(Component, url, route, entries, shell = false) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, retryOnMount: false, staleTime: Infinity, gcTime: Infinity } } });
  for (const [key, data] of entries) {
    client.setQueryData(key, data instanceof Error ? null : data);
    if (data instanceof Error) client.getQueryCache().find({ queryKey: key }).setState({
      data: undefined, status: 'error', error: data, fetchStatus: 'idle',
    });
  }
  try {
    return renderToStaticMarkup(h(QueryClientProvider, { client }, h(MemoryRouter, { initialEntries: [url] },
      h(Routes, {}, shell
        ? h(Route, { element: h(Component) }, h(Route, { path: route, element: h('p', {}, 'Evidence content') }))
        : h(Route, { path: route, element: h(Component) })))));
  } finally { client.clear(); }
}
function detail() {
  return {
    run: { schemaVersion: '1.0', contentHash: null,
      evidence: { integrity: 'intact', imported: true, signatureTrust: 'not_checked' },
      solution: null, subject: { kind: 'MODEL', rawKind: 'model', name: 'Model X', modelId: null, framework: null },
      run: { runId: 'native', timestamp: null, duration: null, verdict: null, kind: null, scenarioCount: 1, harness: null, seed: null },
      git: null, environment: null, agentEval: null },
    runSummary: null,
    scenarios: [{ id: 'case', resultId: 'result-a', state: 'passed', name: 'Categorical decision', passed: true,
      score: null, measurement: null, duration: null, estimatedCost: 0 }],
    runCostBreakdown: null,
    runArtifacts: [],
  };
}
test('native run renders absent metadata and a categorical decision without inventing a score', () => {
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), detail()]]);
  assert.match(html, /Model X/);
  assert.match(html, /not recorded/);
  assert.match(html, /imported; not sealed when it ran/);
  assert.match(html, />PASS</);
  assert.doesNotMatch(html, />NOT MEASURED<|1970|Invalid Date|NaN/);
  assert.match(html, /\$0/);
});
test('run downloads retain the selected source', () => {
  const data = detail();
  data.runArtifacts = [
    { evidenceId: 'E-trace', kind: 'transcript', captured: true, legacyTrace: true, reportFormat: null },
    { evidenceId: 'E-report-md', kind: 'document', captured: true, legacyTrace: false, reportFormat: 'markdown' },
  ];
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), data]]);
  assert.match(html, /\/api\/v1\/runs\/native\/trace\?sourceId=source-a/);
  assert.match(html, /\/api\/v1\/runs\/native\/reports\/markdown\?sourceId=source-a/);
});
test('capture-off run does not advertise trace or report content', () => {
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), detail()]]);
  assert.match(html, /No captured artifacts/);
  assert.doesNotMatch(html, /\/api\/v1\/runs\/native\/trace|\/api\/v1\/runs\/native\/reports/);
});
test('native artifacts use generic downloads and external references stay unavailable', () => {
  const data = detail();
  data.runArtifacts = [
    { evidenceId: 'E-native', kind: 'transcript', description: 'Native transcript', captured: true, legacyTrace: false, reportFormat: null },
    { evidenceId: 'E-external', kind: 'document', description: 'External reference', captured: false, legacyTrace: false, reportFormat: null },
  ];
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), data]]);
  assert.match(html, /\/artifacts\/E-native\?sourceId=source-a/);
  assert.match(html, /Native transcript/);
  assert.match(html, /External reference/);
  assert.match(html, /content not captured/);
  assert.doesNotMatch(html, /\/artifacts\/E-external|\/native\/trace/);
});
test('reports with the same format retain their individual artifact identities', () => {
  const data = detail();
  data.runArtifacts = ['E-one', 'E-two'].map(evidenceId => ({ evidenceId, kind: 'document', captured: true,
    legacyTrace: false, reportFormat: 'md', description: evidenceId }));
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), data]]);
  assert.match(html, /\/artifacts\/E-one\?sourceId=source-a/);
  assert.match(html, /\/artifacts\/E-two\?sourceId=source-a/);
  assert.doesNotMatch(html, /\/reports\/md/);
});
test('trace view reads only the selected source cache and preserves return navigation', () => {
  const html = render(TraceWaterfallPage, '/runs/same/trace?sourceId=source-a', '/runs/:runId/trace', [
    [queryKeys.runs.trace('same', 'source-a'), { runId: 'same', scenarioId: 'case-a', events: [] }],
    [queryKeys.runs.trace('same', 'source-b'), { runId: 'same', scenarioId: 'case-b', events: [
      { timestamp: '2026-10-01T00:00:00Z', kind: 'foreign-event' },
    ] }],
  ]);
  assert.match(html, /The captured trace contains no events/);
  assert.match(html, /href="\/runs\/same\?sourceId=source-a"/);
  assert.match(html, /\/api\/v1\/runs\/same\/trace\?sourceId=source-a/);
  assert.doesNotMatch(html, /foreign-event|Total span/);
});
for (const [name, timestamps, expected] of [
  ['one event', ['2026-10-01T00:00:00Z'], '0.00 ms'],
  ['simultaneous events', ['2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'], '0.00 ms'],
  ['out-of-order events', ['2026-10-01T00:00:02Z', '2026-10-01T00:00:00Z'], '2.00 s'],
]) test(`trace ${name} reports only the observed timestamp span`, () => {
  const html = render(TraceWaterfallPage, '/runs/native/trace?sourceId=source-a', '/runs/:runId/trace', [
    [queryKeys.runs.trace('native', 'source-a'), { runId: 'native', scenarioId: 'case',
      events: timestamps.map(timestamp => ({ timestamp, kind: 'message' })) }],
  ]);
  assert.ok(html.includes(`Observed timestamp span: ${expected}`), html);
  assert.match(html, /Operation durations were not recorded/);
  assert.doesNotMatch(html, /Total span:|median inter-event gap/);
});
test('download URLs encode identifiers and source without changing their identities', () => {
  assert.equal(restUrls.artifact('run:one', 'E-data', 'a/b?c&d'),
    '/api/v1/runs/run%3Aone/artifacts/E-data?sourceId=a%2Fb%3Fc%26d');
  assert.equal(restUrls.trace('run:one'), '/api/v1/runs/run%3Aone/trace');
});
for (const [code, message] of [
  ['trace_viewer_unsupported', 'This captured transcript uses a format this viewer does not support'],
  ['trace_not_captured', 'No captured trace is available for this run'],
  ['run_not_found', 'The selected run source was not found'],
]) test(`trace ${code} preserves the server's availability distinction`, async () => {
  const originalFetch = global.fetch;
  let error;
  try {
    global.fetch = async url => {
      assert.equal(url, '/api/v1/runs/native/trace?sourceId=source-a');
      return { status: 404, json: async () => ({ code }) };
    };
    await assert.rejects(fetchTrace('native', 'source-a'), value => { error = value; return value instanceof Error; });
  } finally { global.fetch = originalFetch; }
  const html = render(TraceWaterfallPage, '/runs/native/trace?sourceId=source-a', '/runs/:runId/trace',
    [[queryKeys.runs.trace('native', 'source-a'), error]]);
  assert.ok(html.includes(message), html);
  assert.doesNotMatch(html, /re-run with tracing|No trace data captured|Trace JSON/);
});
test('scored result remains undecided; measured zero stays visible', () => {
  const data = detail();
  Object.assign(data.scenarios[0], { state: 'scored', passed: null, score: 0 });
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), data]]);
  assert.match(html, />SCORED</);
  assert.doesNotMatch(html, />FAIL<|>PASS</);
});
test('native error census is displayed separately from skipped counts', () => {
  const data = detail();
  data.runSummary = { verdict: null, stats: { total: 1, passed: 0, failed: 0, warnings: 0, skipped: 0 },
    states: [{ state: 'error', count: 1 }], cost: null };
  const html = render(RunDetailPage, '/runs/native?sourceId=source-a', '/runs/:runId',
    [[queryKeys.runs.detail('native', 'source-a'), data]]);
  assert.match(html, /Errors \/ unavailable<\/dt><dd[^>]*>1<\/dd>/);
  assert.match(html, /error: 1/);
});
test('native-only workspace opens its evidence without solution initialization', () => {
  const html = render(AppShell, '/', '/', [
    [queryKeys.workspace.state(), { hasRunEvidence: true, workspace: { initialized: false, root: null, agentEvalVersion: 'test' } }],
    [queryKeys.version(), { mode: 'local', agentEvalVersion: 'test', graphqlEndpoint: '/graphql' }],
  ], true);
  assert.match(html, /Evidence content/);
  assert.doesNotMatch(html, /agenteval init/);
});
test('duplicate run IDs retain distinct source-qualified navigation and unknown dates', () => {
  const runs = ['source-a', 'source-b'].map(sourceId => ({ sourceId, runId: 'same', subjectName: 'Model X',
    verdict: null, timestamp: null, kind: 'MODEL', score: null, durationMs: null, estimatedCost: null }));
  const html = render(RunsListPage, '/runs', '/runs', [[queryKeys.runs.recent(100), { recentRuns: runs }]]);
  assert.match(html, /\/runs\/same\?sourceId=source-a/);
  assert.match(html, /\/runs\/same\?sourceId=source-b/);
  assert.match(html, /not recorded/);
  assert.doesNotMatch(html, /1970|Invalid Date|NaN/);
});
test('a stored hash alone does not claim verified integrity', () => {
  const html = renderToStaticMarkup(h(ManifestHashBadge, { contentHash: 'sha256:abc' }));
  assert.match(html, /integrity not verified/);
  assert.doesNotMatch(html, /bg-green|text-green/);
});
test('subject cards preserve exact reference and display unfamiliar kinds', () => {
  const html = renderToStaticMarkup(h(MemoryRouter, {}, h(SubjectCard, { subject: {
    kind: 'OTHER', rawKind: 'future-agent', ref: 'other:%41', name: 'A', lastVerdict: null, lastRunId: null, trend: [],
  } })));
  assert.match(html, /future-agent/);
  assert.match(html, /subjectRef=other%3A%2541/);
});
test('subject history keeps unscored runs and never mixes matching display names', () => {
  const data = { subject: { identity: { kind: 'AGENT', rawKind: 'agent', ref: 'agent:%41', name: 'A',
    framework: null, modelId: null, sourceProject: null }, lastRun: null },
    recentRuns: [
      { runId: 'selected', sourceId: 'source-a', subjectRef: 'agent:%41', subjectName: 'A', kind: 'AGENT', timestamp: null, score: null, verdict: null },
      { runId: 'unrelated', sourceId: 'source-b', subjectRef: 'agent:A', subjectName: 'A', kind: 'AGENT', timestamp: null, score: null, verdict: null },
    ] };
  const html = render(SubjectDetailPage, '/subjects/agent/A?subjectRef=agent%3A%2541', '/subjects/:kind/:name',
    [[queryKeys.subjects.detail('AGENT', 'A', 'agent:%41'), data]]);
  assert.match(html, /\/runs\/selected\?sourceId=source-a/);
  assert.doesNotMatch(html, /unrelated/);
});
