// Real-browser regressions for the shipped Memory report's untrusted text boundaries.
// Reuses this folder's pinned Playwright dependency; every request is intercepted offline.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright';

const reportUrl = process.argv[2] ? pathToFileURL(resolve(process.argv[2]))
  : new URL('../../src/AgentEval.Memory/Report/report.html', import.meta.url);
const template = await readFile(reportUrl, 'utf8');
const chartTag = /<script\b[^>]*src="https:\/\/cdnjs\.cloudflare\.com\/ajax\/libs\/Chart\.js\/4\.4\.1\/chart\.umd\.min\.js"[^>]*><\/script>/;
assert.match(template, chartTag, 'Expected the existing pinned chart loader');
const chartStub = 'globalThis.Chart=class{constructor(_,c){this.config=c}destroy(){}};Chart.defaults={font:{}};';
const attack = '<img data-report-attack src=x onerror="globalThis.__memoryReportPwned=true">';
const hoverAttack = String.raw`unknown\');globalThis.__memoryReportPwned=true;//`;
const literal = String.raw`O'Reilly \ path <Memory> & "growth"`;
const results = [], coverage = [];
const browser = await chromium.launch();

function fixture(label = 'Agent', category = 'Basic Retention') {
  const baseline = {
    name: label, timestamp: '2026-10-24T00:00:00Z', is_complete: true,
    has_measured_score: true, overall_score: 80, grade: label, stars: 4,
    agent_config: { model_id: label, reducer_strategy: label },
    benchmark: { benchmark_source: 'native' },
    category_results: { [category]: { score: 80, recommendation: label } },
    dimension_scores: { Recall: 80, Temporal: 60, [label]: 100 }
  };
  return {
    manifest: { agent: { name: label }, benchmarks: [{ preset: label, categories: [category],
      baselines: [{ file: 'a.json' }, { file: 'b.json' }] }] },
    baseline,
    sibling: { agent: { name: label } }
  };
}

async function runCase(name, data, check, tamperCdn = false) {
  const page = await browser.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  let session;
  try {
    if (process.env.MEMORY_REPORT_SECURITY_COVERAGE) {
      session = await page.context().newCDPSession(page);
      await session.send('Debugger.enable');
      await session.send('Profiler.enable');
      await session.send('Profiler.startPreciseCoverage', { callCount: true, detailed: true });
    }
    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.hostname === 'fonts.googleapis.com')
        return route.fulfill({ contentType: 'text/css', body: '' });
      if (url.hostname === 'cdnjs.cloudflare.com')
        return route.fulfill({ contentType: 'application/javascript', headers: { 'access-control-allow-origin': '*' }, body: 'globalThis.__badCdnScriptRan=true;' + chartStub });
      if (url.hostname !== 'memory-report.test') return route.abort();
      if (url.pathname.endsWith('/report.html')) {
        // Chart drawing is a test double; the report's complete inline program is unchanged.
        // The separate tampering case uses the actual original CDN tag and browser integrity check.
        const body = tamperCdn ? template : template.replace(chartTag, `<script>${chartStub}</script>`);
        return route.fulfill({ contentType: 'text/html', body });
      }
      if (url.pathname === '/') return route.fulfill({ contentType: 'text/html', body: '<a href="current/">current</a><a href="other/">other</a>' });
      if (url.pathname === '/other/manifest.json') return route.fulfill({ json: data.sibling });
      if (url.pathname.endsWith('/manifest.json')) return route.fulfill({ json: data.manifest });
      if (url.pathname.endsWith('/a.json') || url.pathname.endsWith('/b.json')) return route.fulfill({ json: data.baseline });
      return route.abort();
    });
    await page.goto('http://memory-report.test/current/report.html');
    await page.waitForFunction(() =>
      document.getElementById('gradeHero').textContent.length > 0 ||
      globalThis.__badCdnScriptRan === true ||
      document.getElementById('mastSub').textContent.includes('Chart.js failed to load'));
    await check(page);
    results.push({ name, passed: true });
    console.log(`PASS ${name}`);
  } catch (error) {
    const diagnostics = { pageErrors, header: await page.locator('#mastSub').textContent() };
    results.push({ name, passed: false, error: error.message, diagnostics });
    console.error(`FAIL ${name}: ${error.message}\n${JSON.stringify(diagnostics)}`);
  } finally {
    if (session) {
      const raw = await session.send('Profiler.takePreciseCoverage');
      for (const entry of raw.result) {
        if (!entry.url.startsWith('http://memory-report.test/')) continue;
        const source = await session.send('Debugger.getScriptSource', { scriptId: entry.scriptId });
        if (source.scriptSource.includes('function renderOverview()'))
          coverage.push({ name, ...entry, source: source.scriptSource });
      }
      await session.detach();
    }
    await page.close();
  }
}

try {
  await runCase('untrusted baseline and manifest fields remain literal text', fixture(attack, attack), async page => {
    assert.equal(await page.locator('[data-report-attack]').count(), 0);
    assert.equal(await page.evaluate(() => globalThis.__memoryReportPwned === true), false);
    assert.ok((await page.locator('#mastSub').innerText()).includes(attack));
    assert.ok((await page.locator('#gradeHero').innerText()).includes(attack));
    assert.equal(await page.locator('#kpiGrid .kl').first().textContent(), attack);
    assert.ok((await page.locator('#dimCallout').innerText()).includes(attack));
  });
  await runCase('backslash and quote category cannot execute hover code', fixture('Agent', hoverAttack), async page => {
    const card = page.locator('#kpiGrid .kpi').first();
    await card.hover();
    assert.equal(await page.evaluate(() => globalThis.__memoryReportPwned === true), false);
    assert.equal(await card.locator('.tt-t').textContent(), hoverAttack);
    await page.mouse.move(0, 0);
    assert.equal(await card.locator('.tooltip').evaluate(el => el.classList.contains('show')), false);
  });
  await runCase('benign special characters retain their exact displayed meaning', fixture(literal, literal), async page => {
    assert.equal(await page.locator('#kpiGrid .kl').first().textContent(), literal);
    await page.locator('#kpiGrid .kpi').first().hover();
    assert.equal(await page.locator('#kpiGrid .tt-t').first().textContent(), literal);
    assert.equal(await page.locator('#cmpA option').first().textContent(), `${literal} (80% · complete)`);
  });
  await runCase('report options retain literal names and ordinary navigation', fixture(attack), async page => {
    await page.locator('#reportSwitcher').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#reportSelect option').first().textContent(), attack);
    assert.equal(await page.locator('#reportSelect').inputValue(), 'current');
    await page.locator('#reportSelect').selectOption('other');
    await page.waitForURL('http://memory-report.test/other/report.html');
  });
  await runCase('visible error messages cannot become markup', fixture(), async page => {
    await page.evaluate(text => window.onerror(text, 'fixture', 7), attack);
    assert.equal(await page.locator('[data-report-attack]').count(), 0);
    assert.ok((await page.locator('#mastSub').innerText()).includes(attack));
  });
  const external = fixture();
  external.baseline.benchmark.benchmark_source = attack;
  await runCase('unknown external source labels remain literal text', external, async page => {
    assert.equal(await page.locator('[data-report-attack]').count(), 0);
    // The badge uses CSS uppercase; inspect its literal DOM text before presentation.
    assert.equal(await page.locator('#mastSub .pill').first().textContent(), attack);
  });
  const unmeasured = fixture();
  Object.assign(unmeasured.baseline, { has_measured_score: false, is_complete: false, overall_score: 0, dimension_scores: {} });
  Object.assign(unmeasured.baseline.category_results['Basic Retention'], { errored: true, score: 0, skip_reason: attack });
  await runCase('unmeasured observations and escaped reasons retain their meaning', unmeasured, async page => {
    assert.equal(await page.locator('[data-report-attack]').count(), 0);
    assert.ok((await page.locator('#gradeHero').innerText()).includes('grade not established'));
    assert.ok((await page.locator('#kpiGrid').innerText()).includes('NOT MEASURED'));
    assert.ok((await page.locator('#kpiGrid').innerText()).includes(attack));
    assert.ok((await page.locator('#cmpA option').first().textContent()).includes('n/a · not measured'));
  });
  const zero = fixture('Zero');
  Object.assign(zero.baseline, { overall_score: 0, grade: 'F', stars: 0, dimension_scores: { Recall: 0, Temporal: 0 } });
  zero.baseline.category_results['Basic Retention'].score = 0;
  await runCase('recorded zero remains measured zero', zero, async page => {
    assert.equal(await page.locator('#gradeHero .gn').textContent(), '0.0%');
    assert.equal(await page.locator('#kpiGrid .kv').first().textContent(), '0%');
    assert.ok((await page.locator('#cmpA option').first().textContent()).includes('0% · complete'));
  });
  await runCase('browser blocks tampered CDN bytes and reports unavailable charts', fixture(), async page => {
    assert.equal(await page.evaluate(() => globalThis.__badCdnScriptRan === true), false);
    assert.ok((await page.locator('#mastSub').innerText()).includes('Chart.js failed to load'));
  }, true);
} finally {
  await browser.close();
}

const summary = { reportSha256: createHash('sha256').update(template).digest('hex'),
  passed: results.filter(r => r.passed).length, total: results.length, results,
  limits: 'Offline real browser; chart drawing is a test double, not benchmark/model-quality evidence.' };
if (process.env.MEMORY_REPORT_SECURITY_COVERAGE)
  await writeFile(process.env.MEMORY_REPORT_SECURITY_COVERAGE, JSON.stringify({ ...summary, coverage }, null, 2) + '\n');
console.log(JSON.stringify(summary));
process.exitCode = summary.passed === summary.total ? 0 : 1;
