// UI acceptance against controlled API responses. Run after the SPA production build:
// node scripts/mc-browser-smoke/governance.mjs
// This checks browser behavior; SpaQueriesValidateTests separately checks the real schema.
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
import assert from 'node:assert/strict';

const root = resolve(import.meta.dirname, '../../src/AgentEval.MissionControl/wwwroot');
const server = createServer(async (request, response) => {
  try {
    const path = new URL(request.url, 'http://localhost').pathname;
    const file = path.startsWith('/assets/') ? resolve(root, `.${path}`) : resolve(root, 'index.html');
    if (!file.startsWith(root + sep)) {
      response.writeHead(404).end(); return;
    }
    response.setHeader('Content-Type', { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html' }[extname(file)] ?? 'application/octet-stream');
    response.end(await readFile(file));
  } catch { response.writeHead(404).end(); }
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
let browser;
try {
  browser = await chromium.launch(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {});
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let state = 'AVAILABLE';
  let fail = false;
  let detailState = 'AVAILABLE';
  const requests = [];
  await page.route('**/api/v1/version', route => route.fulfill({ json: { mode: 'local', version: 'test' } }));
  await page.route('**/graphql', async route => {
    const request = route.request().postDataJSON();
    if (request.query.includes('WorkspaceState')) {
      await route.fulfill({ json: { data: { workspace: { initialized: false, root: null, agentEvalVersion: 'test' } } } }); return;
    }
    if (request.query.includes('GovernanceResultDetail')) {
      requests.push(request.variables);
      await route.fulfill({ json: { data: { evidenceResultDetail: {
        state: detailState, code: null, data: detailState === 'AVAILABLE' ? {
          sourceId: request.variables.sourceId, resultId: request.variables.resultId,
          indexInstanceId: 'index-test', indexGeneration: '9223372036854775807',
          runHash: 'run-hash', snapshotDigest: 'snapshot-digest', policyDigest: 'policy-digest', at: request.variables.at,
          sealedState: 'fail', effectiveState: 'pass', overrideEvent: 'override-1', unsealedEvents: 2,
          reviews: [{ target: 'result-1', status: 'approved', event: 'review-1' }],
          assurance: [{ event: 'override-1', shown: 'integrity-only' }],
          sealedJson: '{"message":"<img src=x onerror=alert(1)>"}',
        } : null,
      } } } }); return;
    }
    if (request.query.includes('GovernanceResults')) {
      await route.fulfill({ json: { data: { evidenceResults: {
        state: 'AVAILABLE', code: null, data: {
          indexInstanceId: 'index-test', generation: '9223372036854775806', sourceKind: 'aef',
          verification: 'intact', fingerprint: 'fingerprint-test', nextCursor: null,
          items: [{ resultId: 'result-1', caseId: null, trial: null, outcome: null }],
        },
      } } } }); return;
    }
    requests.push(request.variables);
    if (fail) { await route.fulfill({ status: 503, body: 'Unavailable' }); return; }
    await route.fulfill({ json: { data: { evidenceSources: {
      state, code: null,
      data: ['AVAILABLE', 'EMPTY'].includes(state) ? {
        indexInstanceId: 'index-test', generation: '9223372036854775807', nextCursor: state === 'EMPTY' ? null : 'page-two',
        items: state === 'EMPTY' ? [] : [{ sourceId: 'source-1', rootId: 'fixtures', relativePath: '<img src=x onerror=alert(1)>',
          kind: 'aef', verification: 'Intact', fingerprint: 'abc', indexedGeneration: '9223372036854775806' }],
      } : null,
    } } } });
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/governance`);
  await page.getByText('Connected — evidence sources available', { exact: true }).waitFor();
  assert.equal(await page.getByRole('heading', { name: 'Governance evidence' }).count(), 1);
  assert.equal(await page.getByText('9223372036854775807', { exact: true }).count(), 1);
  assert.equal(await page.locator('main img').count(), 0);
  assert.equal(await page.getByText('<img src=x onerror=alert(1)>', { exact: true }).count(), 1);
  await page.getByLabel('Logical root ID').fill(' fixtures ');
  await page.getByRole('button', { name: 'Apply filter' }).click();
  await page.getByText('Connected — evidence sources available', { exact: true }).waitFor();
  assert.equal(requests.at(-1).rootId, 'fixtures');
  state = 'STALE_CURSOR';
  await page.getByRole('button', { name: 'Next page' }).click();
  await page.getByText('The index changed while browsing.', { exact: false }).waitFor();
  assert.equal(requests.at(-1).cursor, 'page-two');
  assert.equal(await page.locator('table').count(), 0);
  state = 'EMPTY';
  await page.getByRole('button', { name: 'First page' }).click();
  await page.getByText('Connected — no evidence sources match this selection', { exact: true }).waitFor();
  assert.equal(requests.at(-1).cursor, null);
  assert.equal(await page.getByRole('button', { name: 'Next page' }).isDisabled(), true);
  for (const [nextState, text] of [
    ['NOT_CONFIGURED', 'Evidence queries are not configured'], ['NOT_FOUND', 'no longer indexed'],
    ['INVALID_REQUEST', 'was rejected'], ['RESOURCE_LIMIT', 'query budget was reached'],
    ['UNAVAILABLE', 'evidence index is unavailable'], ['FUTURE_STATE', 'unsupported availability state'],
  ]) {
    state = nextState;
    await page.getByRole('button', { name: 'Refresh' }).click();
    await page.getByRole('status').filter({ hasText: text }).waitFor();
    assert.equal(await page.locator('table').count(), 0);
  }
  state = 'AVAILABLE';
  await page.getByRole('button', { name: 'Refresh' }).click();
  await page.locator('table').waitFor();
  fail = true;
  await page.getByRole('button', { name: 'Refresh' }).click();
  await page.getByRole('status').filter({ hasText: 'Connection failed' }).waitFor();
  assert.equal(await page.locator('table').count(), 0);
  fail = false;
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await page.getByRole('link', { name: '<img src=x onerror=alert(1)>', exact: true }).click();
  await page.getByRole('heading', { name: 'Source results', exact: true }).waitFor();
  await page.getByText('No case (aggregate)', { exact: true }).waitFor();
  assert.equal(await page.getByText('Not declared', { exact: true }).count(), 1);
  assert.equal(await page.getByText('Not measured', { exact: true }).count(), 1);
  await page.getByRole('button', { name: 'result-1', exact: true }).click();
  const detail = page.getByRole('region', { name: 'Verified result detail' });
  await detail.getByText('snapshot-digest', { exact: true }).waitFor();
  await detail.getByRole('alert').waitFor();
  assert.equal(await detail.getByText('fail', { exact: true }).count(), 1);
  assert.equal(await detail.getByText('pass', { exact: true }).count(), 1);
  await detail.getByText('Sealed result JSON', { exact: true }).click();
  assert.match(await detail.locator('pre').innerText(), /<img src=x/);
  assert.equal(await detail.locator('img').count(), 0);
  await detail.getByLabel('UTC view time').fill('2026-01-01T00:00:00Z');
  await detail.getByRole('button', { name: 'Apply time' }).click();
  await detail.getByText('2026-01-01T00:00:00Z', { exact: true }).waitFor();
  assert.equal(requests.at(-1).at, '2026-01-01T00:00:00Z');
  detailState = 'UNAVAILABLE';
  await detail.getByRole('button', { name: 'Re-verify' }).click();
  await detail.getByRole('status').filter({ hasText: 'Verified evidence is currently unavailable' }).waitFor();
  assert.equal(await detail.getByText('snapshot-digest', { exact: true }).count(), 0);
  assert.equal(await detail.locator('pre').count(), 0);
  assert.deepEqual(errors, []);
  console.log('Governance browser acceptance passed: workspace, source navigation, exact generations, escaped content, filtering, pagination, refusals, aggregate absences, sealed/effective states, explicit view time, revision mismatch and stale-detail cleanup.');
} finally {
  await browser?.close();
  await new Promise(done => server.close(done));
}
