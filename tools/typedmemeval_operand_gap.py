# -*- coding: utf-8 -*-
"""Is the calibration gate steering the quantity that sets headroom? ZERO model calls.

WHY THIS EXISTS. Every vertical is calibrated to mean realised coverage 0.70 -- the SHARE of a
question's gold a K-budget retriever surfaces. The V9 arm, which headroom is defined from, needs
ALL of it. For gold depth 1 the two are the same function; for depth >= 2 they come apart, and
nothing measured how far.

Measured across the ten shipped corpora, using V9's OWN documents rather than the generator's:

    SHARE    spread 0.195   r(share, headroom)   = +0.004
    ALLgold  spread 0.463   r(ALLgold, headroom) = -1.000  (it IS headroom)

+0.004 is not a weak relationship. The calibration machinery works perfectly on a quantity with no
bearing on the property shapes are accepted against. See MEASUREMENT_STATUS 88.30 and 88.44 -- and
88.44 for why the obvious fix is a CONTRACT change rather than a re-run: an ALLgold target of 0.30
is a SHARE target of 0.30 on the three depth-1 verticals, 0.20 below the band floor ADR-026
declares.

Run it after any change that moves gold depth or the echo knob.
"""
import json, os, sys
sys.path.insert(0, 'C:/git/joslat/AgentEval/tools')
import typedmemeval_common as tmc
R='C:/git/joslat/AgentEval/src/AgentEval.Memory/Data/typedmemeval'

def render(sess, date):
    turns = "\n".join("%s: %s" % (t['role'], t['content']) for t in sess)
    return "### Session 1 (%s)\n%s" % (date, turns)

print('%-14s %7s %8s %9s %9s %9s' % ('vertical','echo','SHARE','ALLgold','head(pred)','depth'))
print('-'*62)
rows=[]
for v in sorted(os.listdir(R)):
    cj=os.path.join(R,v,'agenteval-typedmemeval-%s-v5.json'%v)
    cm=os.path.join(R,v,'agenteval-typedmemeval-%s-v5.meta.json'%v)
    if not os.path.exists(cj): continue
    meta=json.load(open(cm,encoding='utf-8'))
    echo=(meta.get('coverage') or {}).get('echo')
    share=(meta.get('coverage') or {}).get('mean_realised')
    n=allg=0; depths=[]
    for q in json.load(open(cj,encoding='utf-8')):
        ids=q['haystack_session_ids']; gold={i for i,s in enumerate(ids) if s in set(q['answer_session_ids'])}
        if not gold: continue
        docs=[render(s,d) for s,d in zip(q['haystack_sessions'],q['haystack_dates'])]
        top=set(tmc.bm25_rank(q['question'],docs)[:tmc.K_REF])
        n+=1; allg += gold.issubset(top); depths.append(len(gold))
    a=allg/n
    rows.append((v,echo,share,a,1-a,sum(depths)/len(depths)))
    print('%-14s %7s %8.3f %9.3f %9.3f %9.2f' % (v,echo,share,a,1-a,sum(depths)/len(depths)))
import statistics
print()
print('SHARE   spread %.3f  (calibrated target 0.70)' % (max(r[2] for r in rows)-min(r[2] for r in rows)))
print('ALLgold spread %.3f  <-- the quantity that actually sets headroom' % (max(r[3] for r in rows)-min(r[3] for r in rows)))
def corr(xs,ys):
    mx=sum(xs)/len(xs); my=sum(ys)/len(ys)
    sxy=sum((x-mx)*(y-my) for x,y in zip(xs,ys)); sxx=sum((x-mx)**2 for x in xs); syy=sum((y-my)**2 for y in ys)
    return sxy/((sxx*syy)**0.5) if sxx and syy else 0
print('r(SHARE, headroom)   = %+.3f' % corr([r[2] for r in rows],[r[4] for r in rows]))
print('r(ALLgold, headroom) = %+.3f  (it IS headroom, by construction)' % corr([r[3] for r in rows],[r[4] for r in rows]))
print('r(depth, ALLgold)    = %+.3f' % corr([r[5] for r in rows],[r[3] for r in rows]))
