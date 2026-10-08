// Mission Control install-and-open smoke: every page of the installed tool's UI, in a real (headless) browser, over a
// workspace with stored runs. A page fails on a console error, an uncaught exception, a failed request, a GraphQL
// error, an error or empty state ("Failed to load", "No … found", …), a main area with almost no text, or missing the
// data the fixture put there. Usage: node smoke.mjs <base-url> '<ids json>' (run.sh does all of it).
import { chromium } from 'playwright';

const base = process.argv[2];
const ids = JSON.parse(process.argv[3]);

// What the fixture (tests/AgentEval.Tests/MissionControl/Fixtures/MissionControlFixtureBuilder.cs) puts on each page.
const pages = [
  { path: '/', expect: [ids.subjectName] },
  { path: '/subjects', expect: [ids.subjectName] },
  { path: `/subjects/${ids.subjectKind}/${ids.subjectName}`, expect: [ids.subjectName, 'Score over time ('] },
  { path: '/runs', expect: [ids.subjectName], links: [`/runs/${ids.runId}`] },
  { path: `/runs/${ids.runId}`, expect: ['Not measured', ids.runId], links: ['/scenarios/'], followScenario: true },
  { path: `/runs/${ids.runId}/trace`, expect: ['search-flights'] },
  { path: '/compliance', expect: [ids.regulation] },
  // Every matrix cell with evidence shows a known status glyph: a status the UI does not map renders blank or "?".
  { path: `/compliance/${ids.regulation}`, expect: [ids.evidenceName], cells: true },
  { path: `/compliance/${ids.regulation}/${ids.evidenceKind}/${ids.evidenceName}/${ids.evidenceTs}`, expect: [ids.evidenceName, 'art-5'] },
  // The built-in evaluator registry, not fixture data.
  { path: '/evaluators', expect: ['tool_call_success'], links: ['/evaluators/tool_call_success'] },
  { path: '/red-team', expect: ['jailbreak'] },
];

// Error and empty states a page renders instead of its data.
const badTexts = [
  'Failed to load', 'Something went wrong', 'not found', 'No run found', 'No subject found', 'No evidence found',
  'No recursive', 'No trace data captured', 'No compliance data',
];

const browser = await chromium.launch();
const failures = [];

async function visit(path, page_) {
  const page = await browser.newPage();
  const problems = [];
  const bodies = [];
  page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
  page.on('pageerror', e => problems.push(`exception: ${e.message}`));
  page.on('requestfailed', r => problems.push(`request failed: ${r.url()} ${r.failure()?.errorText}`));
  page.on('response', r => {
    if (r.status() >= 400) problems.push(`HTTP ${r.status()}: ${r.url()}`);
    if (r.url().endsWith('/graphql')) {
      // Read before the page closes, so an error in a late response is not lost.
      bodies.push(r.json()
        .then(body => { if (body.errors?.length) problems.push(`GraphQL: ${body.errors.map(e => e.message).join('; ')}`); })
        .catch(() => { /* not JSON: the HTTP status check covers it */ }));
    }
  });

  await page.goto(base + path, { waitUntil: 'networkidle' });
  await page.waitForTimeout(300);
  await Promise.all(bodies);

  const main = (await page.locator('main').innerText().catch(() => '')).trim();
  if (main.length < 20) problems.push(`main area nearly empty (${main.length} chars)`);
  for (const bad of badTexts) {
    if (main.toLowerCase().includes(bad.toLowerCase())) problems.push(`shows "${bad}"`);
  }
  for (const want of page_.expect ?? []) {
    if (!want || !main.toLowerCase().includes(String(want).toLowerCase())) problems.push(`does not show "${want}"`);
  }
  for (const link of page_.links ?? []) {
    if ((await page.locator(`main a[href*="${link}"]`).count()) === 0) problems.push(`no link to ${link}`);
  }
  if (page_.cells) {
    const glyphs = await page.locator('button[aria-label*=", status "]').evaluateAll(bs => bs.map(b => b.innerText.trim()));
    if (glyphs.length === 0) problems.push('no matrix cell with evidence');
    const unknown = glyphs.filter(g => g.length === 0 || g === '?').length;
    if (unknown > 0) problems.push(`${unknown} of ${glyphs.length} matrix cell(s) with evidence show no known status`);
  }

  const scenarioHref = page_.followScenario
    ? await page.locator('main a[href*="/scenarios/"]').first().getAttribute('href').catch(() => null)
    : null;
  await page.close();
  return { problems, scenarioHref };
}

function report(path, problems) {
  console.log(`${problems.length ? 'FAIL' : 'ok  '} ${path}${problems.length ? '\n       ' + problems.join('\n       ') : ''}`);
  if (problems.length) failures.push(path);
}

let visited = 0;
for (const p of pages) {
  const { problems, scenarioHref } = await visit(p.path, p);
  report(p.path, problems);
  visited++;
  if (p.followScenario) {
    if (!scenarioHref) {
      report(`${p.path} (scenario link)`, ['no scenario link to follow']);
    } else {
      // The scenario tree of the fixture's composite run: its child bundles must show.
      const tree = await visit(scenarioHref, { expect: ['Policy bundle', 'Quality bundle'] });
      report(scenarioHref, tree.problems);
      visited++;
    }
  }
}

await browser.close();
console.log(failures.length ? `\n${failures.length} page(s) failed` : `\nall ${visited} pages ok`);
process.exit(failures.length ? 1 : 0);
