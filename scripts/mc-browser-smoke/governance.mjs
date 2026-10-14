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
  const requests = [];
  await page.route('**/api/v1/version', route => route.fulfill({ json: { mode: 'local', version: 'test' } }));
  await page.route('**/graphql', async route => {
    const request = route.request().postDataJSON();
    if (request.query.includes('WorkspaceState')) {
      await route.fulfill({ json: { data: { workspace: { initialized: false, root: null, agentEvalVersion: 'test' } } } }); return;
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
  assert.deepEqual(errors, []);
  console.log('Governance browser acceptance passed: independent workspace, exact generations, escaped content, filtering, pagination, refusal states and failed-refresh cleanup.');
} finally {
  await browser?.close();
  await new Promise(done => server.close(done));
}
