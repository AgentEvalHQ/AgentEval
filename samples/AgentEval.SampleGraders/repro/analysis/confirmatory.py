#!/usr/bin/env python3
# Non-convergence-of-patching analysis (the paper's C1 result), reproducible.
# Pure Python (no numpy/scipy) so it runs anywhere. GLOBS ../data/multiseed-*.json (one per attack family, each emitted by
#   dotnet run --project samples/AgentEval.SampleGraders -- --nonconvergence --family <name> --seeds N
# ). Each seed is one independent corpus draw = the resampling unit.
#
# Outputs, per arm (keyword oracle / production composite) and for the contrast:
#   (A) MODEL-FREE primary: tail-mean = mean fresh-fabrications over the last K rounds; across-seed 95% percentile
#       bootstrap CI. Non-convergence  <=>  the keyword CI lower bound > 0.
#   (B) DECAY-FIT corroborator: pooled fit of  fresh = a + b*exp(-c*r)  via Nelder-Mead; seed-bootstrap CI of the
#       asymptote a. One-sided H0: a = 0  <=>  CI lower bound > 0.
#   (C) CONTRAST: keyword_tail - composite_tail (and a_keyword - a_composite); bootstrap CI > 0.
#
#   python confirmatory.py
import json, math, os, random, sys

try: sys.stdout.reconfigure(encoding="utf-8")  # Windows console is cp1252; the report has ≥/≈/✅
except Exception: pass

random.seed(20260623)  # fixed so the bootstrap is reproducible
HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "data", "multiseed-result.json")  # legacy single-file fallback; main() globs multiseed-*.json
TAIL_K = 3
B = 4000  # bootstrap resamples

def model(r, a, b, c):
    return a + b * math.exp(-c * r)

def sse(params, xs, ys):
    a, b, c = params
    ymax = max(ys) if ys else 1.0
    pen = 0.0
    # Bound the fit so the over-parameterised 3-param exp can't diverge on noisy R=8 data:
    if a < 0.0:               pen += a * a * 1e4 + 1e6          # asymptote (a count) >= 0
    if a > 2 * ymax + 5:      pen += (a - (2 * ymax + 5)) ** 2 * 1e4
    if c < 0.02:              pen += (0.02 - c) ** 2 * 1e6 + 1e5  # require genuine decay, bounded rate
    if c > 5.0:               pen += (c - 5.0) ** 2 * 1e6
    return pen + sum((model(x, a, b, c) - y) ** 2 for x, y in zip(xs, ys))

def nelder_mead(f, x0, step=0.5, max_iter=3000, no_improv_break=40, tol=1e-9):
    dim = len(x0); prev = f(x0); res = [[list(x0), prev]]
    for i in range(dim):
        x = list(x0); x[i] += step; res.append([x, f(x)])
    no_improv = 0
    for _ in range(max_iter):
        res.sort(key=lambda r: r[1]); best = res[0][1]
        if best < prev - tol: prev = best; no_improv = 0
        else:
            no_improv += 1
            if no_improv >= no_improv_break: break
        c0 = [sum(t[0][i] for t in res[:-1]) / dim for i in range(dim)]
        xr = [c0[i] + (c0[i] - res[-1][0][i]) for i in range(dim)]; fr = f(xr)
        if res[0][1] <= fr < res[-2][1]: res[-1] = [xr, fr]; continue
        if fr < res[0][1]:
            xe = [c0[i] + 2.0 * (c0[i] - res[-1][0][i]) for i in range(dim)]; fe = f(xe)
            res[-1] = [xe, fe] if fe < fr else [xr, fr]; continue
        xc = [c0[i] + 0.5 * (res[-1][0][i] - c0[i]) for i in range(dim)]; fc = f(xc)
        if fc < res[-1][1]: res[-1] = [xc, fc]; continue
        x1 = res[0][0]
        res = [[[x1[i] + 0.5 * (t[0][i] - x1[i]) for i in range(dim)], None] for t in res]
        for t in res: t[1] = f(t[0])
    res.sort(key=lambda r: r[1]); return res[0][0]

def fit_asymptote(seeds):
    """Pooled fit over all (round, fresh) points across the given seed trajectories -> asymptote a."""
    xs, ys = [], []
    for fr in seeds:
        for r, y in enumerate(fr, start=1):
            xs.append(r); ys.append(y)
    if not xs: return 0.0
    R = max(xs)
    tail_ys = [y for x, y in zip(xs, ys) if x > R - TAIL_K]
    tail = sum(tail_ys) / max(1, len(tail_ys))
    a0 = max(0.0, tail); b0 = (ys[0] if ys else 0) - a0; c0 = 0.3
    a, b, c = nelder_mead(lambda p: sse(p, xs, ys), [a0, b0, c0])
    return a

def tail_mean(fr):
    t = fr[-TAIL_K:] if len(fr) >= TAIL_K else fr
    return sum(t) / len(t)

def boot_ci(values, stat, b=B, lo=2.5, hi=97.5):
    n = len(values)
    if n == 0: return (0.0, 0.0, 0.0)
    draws = []
    for _ in range(b):
        sample = [values[random.randrange(n)] for _ in range(n)]
        draws.append(stat(sample))
    draws.sort()
    point = stat(values)
    return (point, draws[int(lo / 100 * b)], draws[int(hi / 100 * b)])

def arm_report(name, seeds):
    tails = [tail_mean(fr) for fr in seeds]
    t_pt, t_lo, t_hi = boot_ci(tails, lambda v: sum(v) / len(v))
    a_pt = fit_asymptote(seeds)
    a_boot = boot_ci(list(range(len(seeds))),
                     lambda idx: fit_asymptote([seeds[i] for i in idx]))
    return {
        "n_seeds": len(seeds),
        "tail_mean": t_pt, "tail_ci": (t_lo, t_hi),
        "asymptote": a_pt, "asymptote_ci": (a_boot[1], a_boot[2]),
        "tails": tails,
    }

def uniq(rows):
    seen, out = set(), []
    for r in rows:
        t = tuple(r)
        if t not in seen: seen.add(t); out.append(r)
    return out

def two_sample_diff(kt, ct):
    nk, nc = len(kt), len(ct)
    d_pt = sum(kt) / nk - sum(ct) / nc
    draws = sorted((sum(kt[random.randrange(nk)] for _ in range(nk)) / nk)
                   - (sum(ct[random.randrange(nc)] for _ in range(nc)) / nc) for _ in range(B))
    return d_pt, draws[int(2.5 / 100 * B)], draws[int(97.5 / 100 * B)]

def cluster_boot_ci(fam_lists, b=B):
    """FAMILY-cluster bootstrap: resample the G families WITH replacement, then seeds within each. Honestly reflects
    between-family heterogeneity (wider than a naive i.i.d. pool). With small G it is itself imprecise."""
    G = len(fam_lists)
    point = sum(sum(f) for f in fam_lists) / sum(len(f) for f in fam_lists)
    draws = []
    for _ in range(b):
        vals = []
        for _ in range(G):
            f = fam_lists[random.randrange(G)]
            vals += [f[random.randrange(len(f))] for _ in range(len(f))]
        draws.append(sum(vals) / len(vals))
    draws.sort()
    return point, draws[int(2.5 / 100 * b)], draws[int(97.5 / 100 * b)]

def cluster_contrast(fam_kt, fam_ct, b=B):
    """Cluster bootstrap of the keyword−composite pooled difference (resample families, paired kt/ct within a family)."""
    G = len(fam_kt)
    kpt = sum(sum(f) for f in fam_kt) / sum(len(f) for f in fam_kt)
    cpt = sum(sum(f) for f in fam_ct) / sum(len(f) for f in fam_ct)
    draws = []
    for _ in range(b):
        kv, cv = [], []
        for _ in range(G):
            i = random.randrange(G)
            fk, fc = fam_kt[i], fam_ct[i]
            kv += [fk[random.randrange(len(fk))] for _ in range(len(fk))]
            cv += [fc[random.randrange(len(fc))] for _ in range(len(fc))]
        draws.append(sum(kv) / len(kv) - sum(cv) / len(cv))
    draws.sort()
    return kpt - cpt, draws[int(2.5 / 100 * b)], draws[int(97.5 / 100 * b)]

def analyze_set(kw_all, cp_all, label):
    """Report lines for one keyword/composite set. Returns (lines, kt, ct) where kt/ct are per-seed tail-means (for pooling).
    Keyword is DETERMINISTIC → dedupe (identical trajectory = seed collision). Composite is STOCHASTIC → keep all (identical
    all-zeros can come from different corpora = legitimate independent draws)."""
    kw, cp = uniq(kw_all), cp_all
    L = []
    if len(kw) < len(kw_all):
        L.append(f"> [WARN] keyword seed collision in {label}: {len(kw_all)}→{len(kw)} unique (using unique).")
    K = arm_report("keyword", kw); C = arm_report("composite", cp)
    kt = [tail_mean(fr) for fr in kw]; ct = [tail_mean(fr) for fr in cp]
    d_pt, d_lo, d_hi = two_sample_diff(kt, ct)
    excl = K["tail_ci"][0] > 0
    ksens = " · ".join(f"K={k}: {sum(sum(fr[-k:]) / len(fr[-k:]) for fr in kw) / len(kw):.2f}" for k in (2, 3, 4))
    L += [
        f"### {label}  (keyword {len(kw)} unique seeds · composite {len(cp)} seeds)\n",
        f"- **KEYWORD** tail-mean **{K['tail_mean']:.2f}** (95% CI {K['tail_ci'][0]:.2f}–{K['tail_ci'][1]:.2f}) "
        f"{'— EXCLUDES 0 ✅' if excl else '— includes 0'}; decay â≈{K['asymptote']:.2f} (CI unreliable, R=8); tail-K {ksens}.",
        f"- **COMPOSITE** tail-mean **{C['tail_mean']:.2f}** (CI {C['tail_ci'][0]:.2f}–{C['tail_ci'][1]:.2f}); decay â≈{C['asymptote']:.2f}.",
        f"- **CONTRAST** Δ=**{d_pt:.2f}** (95% CI {d_lo:.2f}–{d_hi:.2f}) {'— EXCLUDES 0 ✅' if d_lo > 0 else '— includes 0'}\n",
    ]
    return L, kt, ct

def main():
    import glob
    files = sorted(glob.glob(os.path.join(HERE, "..", "data", "multiseed-*.json")))
    fams = []
    for fp in files:
        if os.path.basename(fp) == "multiseed-result.json":  # legacy single-file alias — skip to avoid double-count
            continue
        with open(fp) as f: d = json.load(f)
        fams.append((d["config"].get("family", os.path.basename(fp)),
                     [x["fresh"] for x in d["keyword"]], [x["fresh"] for x in d["composite"]]))
    if not fams:
        with open(DATA) as f: d = json.load(f)
        fams = [(d["config"].get("family", "?"), [x["fresh"] for x in d["keyword"]], [x["fresh"] for x in d["composite"]])]

    G = len(fams)
    lines = []; P = lines.append
    P("# Non-convergence analysis — DESCRIPTIVE / EXPLORATORY (not the pre-registered confirmatory test)\n")
    P(f"_Families: {', '.join(f[0] for f in fams)} · gpt-4o-mini · tail K={TAIL_K} · bootstrap B={B} · seed=20260623_\n")
    P("**Reportable estimand = model-free tail-mean (last K rounds). Resampling unit = seed (independent corpus draw). "
      "Keyword deterministic → dedupe = seed-collision guard; composite stochastic → all draws kept. Decay-fit asymptote "
      "is over-parameterised on R=8 (descriptive only). Per-family first, then pooled.**\n")

    P("## (A) Per-family\n")
    fam_kt, fam_ct = [], []
    for name, kw, cp in fams:
        sub, kt, ct = analyze_set(kw, cp, name)
        lines += sub; fam_kt.append(kt); fam_ct.append(ct)

    if len(fams) > 1:
        P("## (B) POOLED across families — FAMILY-CLUSTER bootstrap (resample families, then seeds within)\n")
        kci = cluster_boot_ci(fam_kt)
        cci = cluster_boot_ci(fam_ct)
        d_pt, d_lo, d_hi = cluster_contrast(fam_kt, fam_ct)
        nk = sum(len(f) for f in fam_kt); nc = sum(len(f) for f in fam_ct)
        fam_means = [sum(f) / len(f) for f in fam_kt]
        P("- **Between-family heterogeneity (keyword tail-means):** "
          + " · ".join(f"{fams[i][0]}={fam_means[i]:.2f}" for i in range(len(fams)))
          + f" (spread {max(fam_means) - min(fam_means):.2f}). The pooled point is a between-family average — **the per-family CIs (§A) are the primary evidence.**")
        P(f"- **KEYWORD pooled** tail-mean **{kci[0]:.2f}** (95% CLUSTER CI {kci[1]:.2f}–{kci[2]:.2f}) "
          f"{'— EXCLUDES 0 ✅' if kci[1] > 0 else '— includes 0'}, across **{nk} keyword draws / {len(fams)} families**.")
        P(f"- **COMPOSITE pooled** tail-mean **{cci[0]:.2f}** (cluster CI {cci[1]:.2f}–{cci[2]:.2f}), {nc} composite draws.")
        P(f"- **CONTRAST pooled** Δ=**{d_pt:.2f}** (95% cluster CI {d_lo:.2f}–{d_hi:.2f}) "
          f"{'— EXCLUDES 0 ✅: keyword non-converges where the composite does not, ACROSS families.' if d_lo > 0 else '— includes 0'}")
        P(f"> *The family-cluster CI (resample the {G} families, then seeds within) is WIDER than a naive i.i.d. pool and honestly reflects between-family heterogeneity; with only G={G} families it is itself small-G (imprecise) → the per-family CIs are the primary evidence, the pooled point a convenience average.*\n")

    P("\n_Honest scope: **DESCRIPTIVE / EXPLORATORY**, NOT a pre-registered, FWER-controlled confirmatory test. "
      "gpt-4o-mini; seed-level (independent-corpus-draw) resampling; pooling = a FAMILY-CLUSTER bootstrap (resample families then seeds — "
      f"wider than a naive i.i.d. pool, honestly reflecting between-family heterogeneity; with G={G} families it is small-G (imprecise), so the PER-FAMILY CIs are the primary evidence). "
      "This is still NOT a full templates-within-family random-effects model with a small-G (Cameron-Gelbach-Miller) correction — that is future Stage-2 work. Composite n is small per family (indicative CI). "
      "The decay-fit asymptote is over-parameterised on R=8 (unbounded diverges; bounded a≥0 makes its CI a constraint artifact) → the model-free tail-mean is the reportable estimand._")

    out = os.path.join(HERE, "confirmatory-result.md")
    with open(out, "w", encoding="utf-8") as f: f.write("\n".join(lines))
    print("\n".join(lines))
    print(f"\n[wrote {out}]")

if __name__ == "__main__":
    main()
