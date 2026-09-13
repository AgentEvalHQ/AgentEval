# -*- coding: utf-8 -*-
"""C-E (narrowed): cross-model-line judge agreement, within one vendor.

WHAT THIS IS, AND WHAT IT IS NOT
--------------------------------
C-E was filed as a bound on judge-FAMILY bias. It cannot be that here: no Anthropic, Google or
Mistral key is configured. What IS reachable is the o-series (o3, o4-mini) alongside the shipped
judge's gpt-5.5 -- a different MODEL LINE, same vendor. So the claim is narrowed and renamed rather
than overstated:

    NOT  "judge-family bias is bounded at X"
    BUT  "two judges on a different model line, same vendor, agree with the shipped judge at X"

A cross-vendor bound remains unpurchased and is still the thing that would settle it.

WHY TWO SECOND JUDGES, NOT ONE
------------------------------
One disagreeing judge is ambiguous: it could be the shipped judge being biased, or the new judge
being wrong. Two independent second judges separate those. If o3 and o4-mini disagree with gpt-5.5
in the SAME direction, that is signal about the shipped judge. If they disagree with each other,
the reading is that equivalence judging is noisy at the margin and no bias claim is supportable.

SAMPLING, AND THE WEIGHT THAT MUST TRAVEL WITH ANY NUMBER
---------------------------------------------------------
The 50 cases are stratified by arm and balanced on the shipped judge's own yes/no, because a family
bias shows as a systematic flip in one direction and a cell with no `yes` cases cannot see a
yes->no flip. That balancing DELIBERATELY over-samples the rare class: v2 is 0.3% yes in the live
frame and 57% yes in the sample. So the raw sample disagreement rate is NOT a population rate. Per
cell first, then re-weighted by each cell's true share of the 5,344 live verdicts.

THREE-STAGE PROTOCOL: --dry-run (free, every case), then --one (a single real call), then the full
run. Nothing is written on a dry run.
"""
import argparse
import collections
import json
import os
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
SAMPLE = os.path.join(HERE, 'judge-sample-50.json')
OUT = os.path.join(HERE, 'judge-agreement-results.json')

SECOND_JUDGES = ('o4-mini', 'o3')

PROMPT = (
    "You are checking whether a response conveys the same answer as a reference answer.\n"
    "Ignore wording, length, and extra detail: a short answer that states the same fact is a "
    "match, and a long answer that never states it is not.\n"
    "Reply with exactly one word: yes or no.\n\n"
    "QUESTION\n{question}\n\nREFERENCE ANSWER\n{gold}\n\nRESPONSE\n{response}\n\n"
    "Does the response convey the reference answer?"
)

DRY_STUB = 'DRY-RUN-STUB'


#: Azure api-version, matching run_typedmemeval_probes.py so the second judges are reached exactly
#: the way the shipped judge is. A different api-version would be a second uncontrolled variable.
AZURE_API_VERSION = '2024-12-01-preview'


def ask(model, question, gold, response, dry, provider='openai'):
    """One equivalence judgment. Returns the raw text.

    Two providers, because the claim this instrument can support depends entirely on which judges
    are actually reachable:

      openai  o3 / o4-mini -- a different MODEL LINE, same vendor. Needs credits; currently 429
              insufficient_quota, so this path is unpurchased.
      azure   a second DEPLOYMENT of the same model family as the shipped judge. Weaker, and named
              weaker: "deployment variance within one family". This is the fallback the plan names
              for exactly this situation, and it is reachable today.
    """
    if dry:
        return DRY_STUB
    content = PROMPT.format(question=question, gold=gold, response=response)

    if provider == 'azure':
        endpoint = os.environ.get('AZURE_OPENAI_ENDPOINT', '').rstrip('/')
        key = os.environ.get('AZURE_OPENAI_API_KEY', '')
        if not (endpoint and key):
            raise SystemExit('AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY are not set; '
                             'refusing to guess a credential.')
        # Temperature deliberately unset: this deployment family rejects explicit values, and the
        # shipped judge is sampled at the provider default too.
        req = urllib.request.Request(
            f'{endpoint}/openai/deployments/{model}/chat/completions'
            f'?api-version={AZURE_API_VERSION}',
            data=json.dumps({'messages': [{'role': 'user', 'content': content}],
                             'max_completion_tokens': 2000}).encode('utf-8'),
            headers={'Content-Type': 'application/json', 'api-key': key})
        return _send(req)

    key = os.environ.get('OPENAI_API_KEY', '')
    if not key:
        raise SystemExit('OPENAI_API_KEY is not set; refusing to guess a credential.')
    body = {
        'model': model,
        'messages': [{'role': 'user', 'content': content}],
        'max_completion_tokens': 2000,
    }
    req = urllib.request.Request(
        'https://api.openai.com/v1/chat/completions',
        data=json.dumps(body).encode('utf-8'),
        headers={'Authorization': f'Bearer {key}', 'Content-Type': 'application/json'})
    return _send(req)


def _send(req):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                payload = json.load(r)
            return (payload['choices'][0]['message']['content'] or '').strip()
        except urllib.error.HTTPError as e:
            if e.code in (429, 500, 502, 503) and attempt < 2:
                time.sleep(4 * (attempt + 1))
                continue
            raise
    return ''


def verdict(text):
    t = (text or '').strip().lower()
    if t.startswith('yes'):
        return 'yes'
    if t.startswith('no'):
        return 'no'
    return 'unparseable'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dry-run', action='store_true',
                    help='stub every call; exercises the real path and writes nothing')
    ap.add_argument('--one', action='store_true', help='one real call, then stop')
    ap.add_argument('--models', default=','.join(SECOND_JUDGES))
    ap.add_argument('--provider', choices=('openai', 'azure'), default='openai',
                    help="'openai' = o3/o4-mini, a different MODEL LINE (needs credits). "
                         "'azure' = a second DEPLOYMENT of the shipped judge's own family, "
                         'which supports only the weaker "deployment variance within one '
                         'family" claim. The provider chosen CHANGES WHAT MAY BE CLAIMED and is '
                         'recorded in the results file.')
    args = ap.parse_args()

    cases = json.load(open(SAMPLE, encoding='utf-8'))['cases']
    models = [m for m in args.models.split(',') if m]
    claim = ('two judges on a different MODEL LINE, same vendor' if args.provider == 'openai'
             else 'deployment variance within ONE model family (the weaker fallback)')
    print('provider=%s  ->  claim supported: %s' % (args.provider, claim))
    print('cases=%d  second judges=%s  %s'
          % (len(cases), ','.join(models),
             'DRY RUN (nothing written)' if args.dry_run else ('ONE REAL CALL' if args.one else 'FULL RUN')))

    results = []
    calls = 0
    for i, case in enumerate(cases):
        row = {k: case[k] for k in ('cache_key', 'arm', 'vertical', 'judge1')}
        for model in models:
            raw = ask(model, case['question'], case['gold'], case['response'], args.dry_run,
                      provider=args.provider)
            calls += 0 if args.dry_run else 1
            row[model] = verdict(raw)
            row[model + '_raw'] = raw[:80]
            if args.one and not args.dry_run:
                print(json.dumps(row, indent=2)[:900])
                print('\none real call made; stopping. calls=%d' % calls)
                return
        results.append(row)
        if not args.dry_run and (i + 1) % 10 == 0:
            print('  %d/%d' % (i + 1, len(cases)), flush=True)

    if args.dry_run:
        print('dry run complete: %d cases x %d judges exercised, calls=0, nothing written'
              % (len(cases), len(models)))
        return

    json.dump({'models': models, 'provider': args.provider, 'claim_supported': claim,
               'cases': results}, open(OUT, 'w', encoding='utf-8'),
              ensure_ascii=False, indent=2)
    print('calls=%d  written: %s' % (calls, OUT))


main()
