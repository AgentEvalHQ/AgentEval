# -*- coding: utf-8 -*-
"""C-E: judge agreement against second judges, and what the second judges let you claim.

WHAT THIS IS
------------
C-E was filed as a bound on judge-FAMILY bias. It is now that. Two non-OpenAI models were
deployed on 2026-09-14 -- Llama-3.3-70B-Instruct (Meta) and Mistral-Large-3 (Mistral AI) -- and
agreed with the shipped gpt-5 judge at 0.99910 and 0.99852 once re-weighted to the live
population of 5,254 verdicts -- raw 45/48 and 42/48 on a sample that deliberately over-samples
the rare class, which is why the raw figure is not the one to quote.

Seven of the 48 rows carry a disagreement from at least one second judge. On 2 of them BOTH
second judges differ, and in the SAME direction (shipped `no` -> both `yes`); those sit in the
`v1/no` and `v8/no` cells, 0.06% and 0.21% of the frame. The other 5 are single-judge, and 4 of
those are Mistral alone in `v2/yes`, a cell holding 0.08%. No cell above 1% of the frame shows a
single disagreement from either judge.

The claim is NOT hard-coded. `_claim_for` derives it from the models this run actually used, and
it walks the whole ladder:

    judge-FAMILY bias across VENDORS          <- what the run above supports
    different MODEL LINE, same vendor         <- what o3/o4-mini support
    deployment variance within ONE line       <- the weakest, and it bounds nothing
    UNVERIFIED provenance / UNVERIFIED line   <- refuses to claim at all

That last rung matters more than it looks. On Azure the `--models` string is a DEPLOYMENT ALIAS
the resource owner chose, not a model: an OpenAI deployment named `judge-primary` and a Llama
deployment named `gpt-4o` are both legal. The alias is resolved against the deployments listing
before anything is claimed, and a real run REFUSES to start when an alias will not resolve --
see `main`.

WHY TWO SECOND JUDGES, NOT ONE
------------------------------
One disagreeing judge is ambiguous: it could be the shipped judge being biased, or the new judge
being wrong. Two independent second judges separate those. If o3 and o4-mini disagree with gpt-5.5
in the SAME direction, that is signal about the shipped judge. If they disagree with each other,
the reading is that equivalence judging is noisy at the margin and no bias claim is supportable.

SAMPLING, AND THE WEIGHT THAT MUST TRAVEL WITH ANY NUMBER
---------------------------------------------------------
The 48 cases are stratified by arm and balanced on the shipped judge's own yes/no, because a family
bias shows as a systematic flip in one direction and a cell with no `yes` cases cannot see a
yes->no flip. That balancing DELIBERATELY over-samples the rare class: v2 is 0.3% yes in the live
frame and 57% yes in the sample. So the raw sample disagreement rate is NOT a population rate. Per
cell first, then re-weighted by each cell's true share of the 5,254 live verdicts.

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
            # THE TOKEN-LIMIT PARAMETER IS VENDOR-SPECIFIC, and this tool assumed OpenAI's spelling
            # for its whole life because every judge it had ever run was OpenAI. Mistral rejects
            # `max_completion_tokens` outright (422 extra_forbidden) and wants `max_tokens`. The
            # moment C-E became cross-vendor -- which is the entire point of C-E -- the request
            # schema stopped being one schema.
            data=json.dumps({'messages': [{'role': 'user', 'content': content}],
                             _token_limit_key(model): 2000}).encode('utf-8'),
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


#: Deployments whose underlying model is NOT OpenAI. Filled in by _resolve_azure at start-up so
#: the request schema can follow the vendor rather than the endpoint.
_NON_OPENAI_DEPLOYMENTS = set()


def _token_limit_key(model: str) -> str:
    """`max_tokens` for non-OpenAI models, `max_completion_tokens` for OpenAI ones."""
    return 'max_tokens' if model in _NON_OPENAI_DEPLOYMENTS else 'max_completion_tokens'


def _send(req):
    # RETRY ON THE SERVICE'S TIMESCALE. 3 attempts backing off 4/8 seconds lost a run against a
    # freshly deployed model: a GlobalStandard deployment's limit is TOKENS PER MINUTE, so every
    # retry inside the first few seconds argues with a window that has not moved. Same correction
    # already made in typedmemeval_dense_retrieval.py -- applied-once, found again here.
    for attempt in range(8):
        try:
            with urllib.request.urlopen(req, timeout=180) as r:  # DevSkim: ignore DS137138
                payload = json.load(r)
            return (payload['choices'][0]['message']['content'] or '').strip()
        except urllib.error.HTTPError as e:
            if e.code not in (429, 500, 502, 503, 504) or attempt == 7:
                raise
            hinted = e.headers.get('Retry-After') if e.headers else None
            if hinted and str(hinted).strip().isdigit():
                delay = min(90, max(5, int(str(hinted).strip())))
            else:
                delay = min(60, 5 * 2 ** attempt)
            print('    %d, waiting %ds' % (e.code, delay), flush=True)
            time.sleep(delay)
    return ''


def verdict(text):
    t = (text or '').strip().lower()
    if t.startswith('yes'):
        return 'yes'
    if t.startswith('no'):
        return 'no'
    return 'unparseable'


#: The model line the SHIPPED judge belongs to. The claim is a statement about the distance
#: between this and the second judges, so it has to be named rather than assumed.
SHIPPED_JUDGE_LINE = 'gpt-5'

#: Prefixes that identify a vendor/family. Anything not matching OpenAI's lines is a different
#: VENDOR, which is the only thing that settles judge-family bias.
_OPENAI_PREFIXES = ('gpt-', 'o1', 'o3', 'o4', 'text-', 'chatgpt')


#: Model ids whose LINE is not recoverable from the prefix. `gpt-chat-latest` is the gpt-5 chat
#: model; reading it as its own line made the §88.19 run look like a cross-LINE comparison when it
#: was within-line -- overstating, which is the direction that matters.
_LINE_ALIASES = {
    'gpt-chat-latest': 'gpt-5',
    'gpt-5-chat-latest': 'gpt-5',
}


def _line_of(model: str):
    """The model LINE (gpt-5 / gpt-4 / o3), or None when it cannot be determined.

    RETURNS None RATHER THAN GUESSING. The old version fell back to the first hyphen-separated
    token, so any unrecognised id became its own "line" and the claim asserted a difference it had
    not established. An unknown line has to block the claim, not decorate it.
    """
    m = model.lower()
    if m in _LINE_ALIASES:
        return _LINE_ALIASES[m]
    for p in ('gpt-5', 'gpt-4', 'o4', 'o3', 'o1'):
        if m.startswith(p):
            return p
    return None


def _resolve_azure(models):
    """deployment alias -> the model actually behind it, or None where it cannot be resolved.

    ON AZURE THE NAME PASSED IS A DEPLOYMENT ID THE USER CHOSE, not a model. An OpenAI deployment
    called `judge-primary` and a Llama deployment called `gpt-4o` are both legal, so classifying the
    claim on the string is classifying on a label that carries no guarantee -- and the claim would
    be wrong in BOTH directions. The deployments listing carries the real mapping; where it cannot
    be reached, the claim must say the provenance is unverified rather than assume it.
    """
    endpoint = (os.environ.get('AZURE_OPENAI_ENDPOINT') or '').rstrip('/')
    key = os.environ.get('AZURE_OPENAI_API_KEY') or ''
    out = {m: None for m in models}
    if not (endpoint and key):
        return out
    try:
        req = urllib.request.Request(
            endpoint + '/openai/deployments?api-version=2023-03-15-preview',
            headers={'api-key': key})
        with urllib.request.urlopen(req, timeout=60) as r:  # DevSkim: ignore DS137138
            data = json.loads(r.read().decode('utf-8'))
    except Exception:
        return out
    mapping = {d.get('id'): d.get('model') for d in data.get('data', [])}
    for m in models:
        out[m] = mapping.get(m)
    return out


def _claim_for(shipped_line: str, models, resolved=None) -> str:
    """What this RUN may claim, derived from the models actually used.

    It used to be derived from `--provider`: openai meant "different model line" and azure meant
    "deployment variance within one family". That is the wrong operand. The provider says where the
    call is routed; the MODELS say how far the second judges sit from the shipped one, and that is
    the whole content of the claim. Running gpt-4.1/gpt-4o through the azure provider is a different
    LINE and was being reported as the weaker within-family fallback.
    """
    if not models:
        return 'nothing -- no second judge was named'

    # Classify on the RESOLVED model where one is available. `resolved` is None for providers whose
    # model string is the model (direct OpenAI); on Azure it is the deployment->model mapping.
    if resolved is not None:
        unresolved = [m for m in models if not resolved.get(m)]
        if unresolved:
            return ('UNVERIFIED provenance -- %s could not be resolved to an underlying model, and '
                    'on this provider the name is a deployment alias the user chose. No family or '
                    'line claim can be made from it.' % ', '.join(unresolved))
        effective = [resolved[m] for m in models]
    else:
        effective = list(models)

    foreign = [m for m in effective if not m.lower().startswith(_OPENAI_PREFIXES)]
    if foreign:
        return ('judge-FAMILY bias across VENDORS -- the claim C-E was filed for '
                '(second judges: %s)' % ', '.join(foreign))
    lines = {_line_of(m) for m in effective}
    if None in lines:
        unknown = sorted(m for m in effective if _line_of(m) is None)
        return ('UNVERIFIED line -- %s is same-vendor but its model line could not be determined, '
                'so a cross-line claim is not established. Add it to _LINE_ALIASES once known.'
                % ', '.join(unknown))
    if lines - {shipped_line}:
        return ('two judges on a different MODEL LINE (%s vs the shipped %s), same vendor'
                % ('/'.join(sorted(lines)), shipped_line))
    return ('deployment variance within ONE model line (%s) -- the weakest form, and it does not '
            'bound family bias' % shipped_line)


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
    resolved = _resolve_azure(models) if args.provider == 'azure' else None
    if resolved:
        _NON_OPENAI_DEPLOYMENTS.update(
            dep for dep, real in resolved.items()
            if real and not real.lower().startswith(_OPENAI_PREFIXES))
    # FAIL BEFORE SPENDING, NOT AFTER. An unresolved alias used to print `UNVERIFIED` and then
    # proceed into 96 paid calls, and it took BOTH things the resolution feeds with it:
    #   * the request schema -- `_token_limit_key` falls back to `max_completion_tokens`, which is
    #     the 422 `extra_forbidden` on a Mistral deployment that this resolution exists to avoid;
    #   * the claim -- an unresolved run can only ever report UNVERIFIED provenance, so every
    #     verdict it buys is unpublishable the moment it is written.
    # A run whose output cannot support any claim is not a cheaper run, it is a purchase with no
    # deliverable. --dry-run is deliberately exempt: it spends nothing and its whole job is to
    # exercise this path when the listing is unreachable.
    if resolved is not None and not args.dry_run:
        unresolved = sorted(dep for dep, real in resolved.items() if not real)
        if unresolved:
            raise SystemExit(
                'refusing to start: %s did not resolve to an underlying model.\n'
                'On this provider the name is a deployment alias, so an unresolved run would (a) '
                'guess the vendor-specific token-limit parameter and (b) be able to claim nothing '
                'but UNVERIFIED provenance -- paying for %d calls whose result cannot be '
                'published.\nCheck the deployment exists and that AZURE_OPENAI_ENDPOINT / '
                'AZURE_OPENAI_API_KEY reach it, or use --dry-run.'
                % (', '.join(unresolved), len(cases) * len(models)))

    claim = _claim_for(SHIPPED_JUDGE_LINE, models, resolved)
    print('provider=%s  judges=%s' % (args.provider, ','.join(models)))
    if resolved:
        print('  deployment -> model: %s'
              % ', '.join('%s=%s' % (k, v or 'UNRESOLVED') for k, v in sorted(resolved.items())))
    print('  -> claim supported: %s' % claim)
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
