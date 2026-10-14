// Actual production host + SQLite + sealed evidence. No HTTP interception or mocked replies.
// Build the SPA/host and generate a fresh governance-fixture first; pass its fixture.json.
import assert from 'node:assert/strict';
import { readFile, appendFile, writeFile } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const fixture = JSON.parse(await readFile(process.argv[2], 'utf8'));
const port = Number(process.env.MC_SMOKE_PORT ?? 5057);
const url = `http://127.0.0.1:${port}`;
const host = resolve(root, 'src/AgentEval.MissionControl');
// Refuse an occupied port instead of accidentally testing an unrelated service.
try {
  await fetch(url, { signal: AbortSignal.timeout(1000) });
  throw new Error(`Port ${port} already serves HTTP`);
} catch (error) { if (error.message.includes('already serves')) throw error; }
const log = createWriteStream(resolve(fixture.workspace, 'host.log'));
const server = spawn('dotnet', [resolve(host, 'bin/Debug/net10.0/AgentEval.MissionControl.dll'),
  '--urls', url, '--contentRoot', host, '--webroot', resolve(host, 'wwwroot'),
  ...Object.entries(fixture.configuration).flatMap(([key, value]) => [`--${key}`, value])],
  { cwd: host, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
server.stdout.pipe(log); server.stderr.pipe(log);
let startError;
server.on('error', error => { startError = error; });
let browser;
let page;
try {
  const deadline = Date.now() + 60000;
  while (true) {
    if (startError) throw startError;
    if (server.exitCode !== null) throw new Error(`Host exited ${server.exitCode}; inspect ${fixture.workspace}/host.log`);
    try { if ((await fetch(url, { signal: AbortSignal.timeout(1000) })).ok) break; } catch { /* bounded startup */ }
    if (Date.now() > deadline) throw new Error('Host startup timed out');
    await new Promise(done => setTimeout(done, 200));
  }
  browser = await chromium.launch({ headless: true, ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.setDefaultTimeout(15000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`${url}/governance`);
  await page.getByRole('link', { name: 'offline-run', exact: true }).click();
  await page.getByRole('heading', { name: 'Source results', exact: true }).waitFor();
  await page.getByRole('button', { name: fixture.resultId, exact: true }).click();
  const detail = page.getByRole('region', { name: 'Verified result detail' });
  await detail.getByText('offline reviewed result', { exact: true }).waitFor();
  assert.match(await detail.innerText(), /2026-10-01T10:15:00.123456789Z/);
  assert.match(await detail.innerText(), /signed/);
  assert.match(await detail.innerText(), /warn/);
  await page.getByRole('link', { name: 'Metrics', exact: true }).click();
  await page.getByRole('cell', { name: '0.9', exact: true }).waitFor();

  await page.getByRole('link', { name: 'Evidence sources', exact: true }).click();
  await page.locator(`a[href="/governance/sources/${fixture.observationId}?view=spans"]`).click();
  await page.getByRole('cell', { name: '18446744073709551615', exact: true }).waitFor();
  await page.getByRole('link', { name: 'Fields', exact: true }).click();
  await page.getByRole('cell', { name: '9007199254740993', exact: true }).waitFor();
  await page.getByRole('cell', { name: 'Conflict', exact: true }).waitFor();
  const conflict = page.getByRole('row').filter({ hasText: 'tokens.output' });
  await conflict.locator('summary').click();
  assert.match(await conflict.innerText(), /12/);
  assert.match(await conflict.innerText(), /13/);

  await page.getByRole('link', { name: 'Evidence sources', exact: true }).click();
  await page.getByRole('link', { name: 'Requirements status', exact: true }).click();
  await page.getByLabel('Subject reference').fill('agent:offline');
  await page.getByLabel('Subject version').fill('1');
  await page.getByLabel('Evidence source IDs').fill(fixture.sourceId);
  await page.getByLabel('UTC view time').fill('2026-10-01T11:00:00Z');
  await page.getByRole('button', { name: 'Resolve requirements' }).click();
  const report = page.getByRole('region', { name: 'Requirement report' });
  await report.getByRole('cell', { name: 'waived', exact: true }).waitFor();
  assert.equal(await report.getByRole('cell', { name: 'not_met', exact: true }).count(), 2);
  await report.locator('summary').filter({ hasText: 'Waiver history (1)' }).click();
  assert.match(await report.innerText(), /Authorized for this requirement/);
  assert.match(await report.innerText(), /Active at view time/);
  await page.getByLabel('UTC view time').fill('2026-10-01T13:00:00Z');
  await page.getByRole('button', { name: 'Resolve requirements' }).click();
  await report.getByRole('cell', { name: 'not_met', exact: true }).nth(2).waitFor();
  await report.locator('summary').filter({ hasText: 'Waiver history (1)' }).click();
  assert.match(await report.innerText(), /Inactive at view time/);

  await page.getByRole('link', { name: 'Evidence sources', exact: true }).click();
  await page.getByRole('link', { name: 'Checkpoint evidence', exact: true }).click();
  await page.getByLabel('Configured checkpoint').selectOption('release');
  await page.getByLabel('Evidence source IDs').fill(fixture.sourceId);
  await page.getByLabel('UTC view time').fill('2026-10-01T11:00:00.123456789Z');
  await page.getByRole('button', { name: 'Explain checkpoint' }).click();
  const checkpoint = page.getByRole('region', { name: 'Checkpoint explanation' });
  await checkpoint.getByText('cp:offline', { exact: true }).waitFor();
  await checkpoint.getByRole('cell', { name: 'passed', exact: true }).waitFor();
  assert.match(await checkpoint.innerText(), /VERIFIED/);
  assert.match(await checkpoint.innerText(), /Established signers: reviewer:offline/);
  assert.ok((await checkpoint.innerText()).includes(`Anchored run hashes: ${fixture.hash}`));
  await page.getByLabel('Evidence source IDs').fill('');
  await page.getByRole('button', { name: 'Explain checkpoint' }).click();
  await checkpoint.getByText('No indexed selection', { exact: true }).first().waitFor();
  assert.equal(await checkpoint.getByRole('cell', { name: 'passed', exact: true }).count(), 0);
  assert.match(await checkpoint.innerText(), /Anchored run hashes: None/);

  // Mutate only this throwaway fixture after successful verification; cached index rows
  // must not allow the host to return a previously successful explanation.
  await appendFile(resolve(fixture.directory, 'results.jsonl'), '\n{"tampered":true}\n');
  await page.getByLabel('Evidence source IDs').fill(fixture.sourceId);
  await page.getByRole('button', { name: 'Explain checkpoint' }).click();
  await page.getByRole('status').filter({ hasText: 'Evidence is currently unavailable' }).waitFor();
  assert.equal(await checkpoint.count(), 0);
  assert.deepEqual(errors, []);
  console.log('Live governance journey passed: real host/index, metrics, exact observations and conflicts, signed decisions, waiver expiry, checkpoint trust, missing evidence and tamper refusal.');
} catch (error) {
  if (page) await writeFile(resolve(fixture.workspace, 'browser-failure.txt'), await page.locator('body').innerText());
  throw error;
} finally {
  await browser?.close();
  if (server.exitCode === null && server.pid) {
    const exited = once(server, 'exit');
    server.kill('SIGTERM');
    const kill = setTimeout(() => server.kill('SIGKILL'), 5000);
    await exited;
    clearTimeout(kill);
  }
  log.end();
}
