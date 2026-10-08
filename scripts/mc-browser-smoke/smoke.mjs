// Mission Control install-and-open smoke: every page of the installed tool's UI, in a real (headless) browser, over a
// workspace with stored runs. A page fails on a console error, an uncaught exception, a failed request, a GraphQL
// error, the UI's own "Failed to load" / "Something went wrong" state, or missing data it should show.
// Usage: node smoke.mjs <base-url> '<ids json>' (run.sh does all of it).
import { chromium } from 'playwright';

const base = process.argv[2];
const ids = JSON.parse(process.argv[3]);

const pages = [
  { path: '/', expect: [ids.subjectName] },
  { path: '/subjects', expect: [ids.subjectName] },
  { path: `/subjects/${ids.subjectKind}/${ids.subjectName}`, expect: [ids.subjectName] },
  { path: '/runs', expect: [ids.subjectName], link: `/runs/${ids.runId}` },
  { path: `/runs/${ids.runId}`, expect: ['Not measured', ids.runId], scenarioLink: true },
  { path: `/runs/${ids.runId}/trace`, expect: [] },
  { path: '/compliance', expect: [ids.regulation] },
  // Every matrix cell that has evidence must show its status glyph (a status the UI did not map rendered blank).
  { path: `/compliance/${ids.regulation}`, expect: [ids.evidenceName], cells: true },
  { path: `/compliance/${ids.regulation}/${ids.evidenceKind}/${ids.evidenceName}/${ids.evidenceTs}`, expect: [ids.evidenceName] },
  { path: '/evaluators', expect: [] },
  { path: '/red-team', expect: ['jailbreak'] },
];

const browser = await chromium.launch();
const failures = [];

async function visit(path, expect, link, cells) {
  const page = await browser.newPage();
  const problems = [];
  page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
  page.on('pageerror', e => problems.push(`exception: ${e.message}`));
  page.on('requestfailed', r => problems.push(`request failed: ${r.url()} ${r.failure()?.errorText}`));
  page.on('response', async r => {
    if (r.status() >= 400) problems.push(`HTTP ${r.status()}: ${r.url()}`);
    if (r.url().endsWith('/graphql')) {
      try {
        const body = await r.json();
        if (body.errors?.length) problems.push(`GraphQL: ${body.errors.map(e => e.message).join('; ')}`);
      } catch { /* not JSON: the HTTP status check covers it */ }
    }
  });

  await page.goto(base + path, { waitUntil: 'networkidle' });
  await page.waitForTimeout(300);
  const text = await page.locator('body').innerText();
  if (text.trim().length < 40) problems.push('blank page');
  for (const bad of ['Failed to load', 'Something went wrong']) {
    if (text.includes(bad)) problems.push(`shows "${bad}"`);
  }
  for (const want of expect) {
    if (!text.toLowerCase().includes(String(want).toLowerCase())) problems.push(`does not show "${want}"`);
  }

  if (link && (await page.locator(`a[href="${link}"]`).count()) === 0) problems.push(`no link to ${link}`);
  if (cells) {
    const glyphs = await page.locator('button[aria-label*=", status "]').evaluateAll(bs => bs.map(b => b.innerText.trim()));
    if (glyphs.length === 0) problems.push('no matrix cell with evidence');
    const blank = glyphs.filter(g => g.length === 0).length;
    if (blank > 0) problems.push(`${blank} of ${glyphs.length} matrix cell(s) with evidence show no status`);
  }

  let scenarioHref = null;
  if (expect.scenarioLink) {
    scenarioHref = await page.locator('a[href*="/scenarios/"]').first().getAttribute('href').catch(() => null);
  }

  await page.close();
  return { problems, text, scenarioHref };
}

for (const p of pages) {
  const expect = Object.assign([...p.expect], { scenarioLink: p.scenarioLink });
  const { problems, scenarioHref } = await visit(p.path, expect, p.link, p.cells);
  console.log(`${problems.length ? 'FAIL' : 'ok  '} ${p.path}${problems.length ? '\n       ' + problems.join('\n       ') : ''}`);
  if (problems.length) failures.push(p.path);

  if (p.scenarioLink) {
    if (!scenarioHref) {
      console.log(`FAIL ${p.path}: no scenario link to follow`);
      failures.push(`${p.path} (scenario link)`);
    } else {
      const tree = await visit(scenarioHref, []);
      console.log(`${tree.problems.length ? 'FAIL' : 'ok  '} ${scenarioHref}${tree.problems.length ? '\n       ' + tree.problems.join('\n       ') : ''}`);
      if (tree.problems.length) failures.push(scenarioHref);
    }
  }
}

await browser.close();
console.log(failures.length ? `\n${failures.length} page(s) failed` : `\nall ${pages.length + 1} pages ok`);
process.exit(failures.length ? 1 : 0);
