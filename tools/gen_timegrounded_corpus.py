"""Generates the AgentEval time-grounded probe corpus.

Every conversation date is written as a real calendar date with the correct weekday
abbreviation, and every gold answer's absolute date is *derived* from those dates rather
than typed by hand, so the arithmetic in the corpus cannot drift from the arithmetic in
the answers.

Corpus rule that gives the probe its teeth: no message content may contain an absolute
date or a four-digit year. Every temporal expression in the conversation is relative
("in three weeks", "the first Monday of next month"), so resolving it requires the
session's own timestamp. Verified at the bottom of this file and again by a C# test.
"""

import json
import os
import re
from datetime import datetime, timedelta

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(
    REPO_ROOT, "src", "AgentEval.Memory", "Data", "longmemeval", "timegrounded",
    "agenteval-timegrounded-v1.json")

DATE_LIKE = re.compile(r"\b(19|20)\d{2}\b|\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b|\b\d{4}[/-]\d{1,2}[/-]\d{1,2}\b")


def d(y, m, day, hh, mm):
    return datetime(y, m, day, hh, mm)


def stamp(dt):
    return f"{dt.year:04d}/{dt.month:02d}/{dt.day:02d} ({dt.strftime('%a')}) {dt.hour:02d}:{dt.minute:02d}"


def human(dt):
    return f"{dt.day} {dt.strftime('%B')} {dt.year}"


def first_weekday_of_next_month(dt, weekday):
    """weekday: 0=Monday."""
    year = dt.year + (1 if dt.month == 12 else 0)
    month = 1 if dt.month == 12 else dt.month + 1
    cursor = datetime(year, month, 1, 9, 0)
    while cursor.weekday() != weekday:
        cursor += timedelta(days=1)
    return cursor


def add_months(dt, months):
    month = dt.month - 1 + months
    year = dt.year + month // 12
    month = month % 12 + 1
    day = min(dt.day, [31, 29 if year % 4 == 0 and (year % 100 != 0 or year % 400 == 0) else 28,
                       31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1])
    return dt.replace(year=year, month=month, day=day)


def turn(role, content, has_answer=False):
    return {"role": role, "content": content, "has_answer": has_answer}


entries = []


def entry(qid, qtype, question, answer, asked, sessions, gold_indexes):
    """sessions: list of (datetime, [turn, ...])."""
    session_ids = [f"{qid}-s{i + 1}" for i in range(len(sessions))]
    entries.append({
        "question_id": qid,
        "question_type": qtype,
        "question": question,
        "answer": answer,
        "question_date": stamp(asked),
        "haystack_sessions": [turns for _, turns in sessions],
        "haystack_dates": [stamp(when) for when, _ in sessions],
        "haystack_session_ids": session_ids,
        "answer_session_ids": [session_ids[i] for i in gold_indexes],
    })


# ── 1. as-of: which gym, at a past moment ────────────────────────────────────────────
s1 = d(2026, 1, 12, 18, 30)
s2 = d(2026, 2, 9, 7, 45)
s3 = d(2026, 3, 23, 19, 10)
switch = s3 - timedelta(days=7)
entry(
    "tg-asof-001", "temporal-as-of",
    "Which gym was I a member of at the start of March?",
    f"Riverside Fitness. You joined Riverside in mid-January and did not move to Northgate "
    f"Athletic until the week of {human(switch)}, so at the start of March you were still at "
    f"Riverside.",
    d(2026, 5, 4, 9, 0),
    [
        (s1, [
            turn("user", "Signed up at Riverside Fitness this evening — three months up front.", True),
            turn("assistant", "Nice, three months is a decent commitment. What made you pick Riverside?"),
            turn("user", "It is two streets away and the rowing machines are never busy."),
            turn("assistant", "Proximity beats everything for keeping a habit. Enjoy it."),
        ]),
        (s2, [
            turn("user", "Any advice for keeping oat milk from splitting in hot coffee?"),
            turn("assistant", "Warm the milk gently first and pour the coffee into it rather than the other way round."),
        ]),
        (s3, [
            turn("user", "I switched to Northgate Athletic last week — Riverside got far too crowded in the evenings.", True),
            turn("assistant", "How is Northgate compared with what you had?"),
            turn("user", "Quieter, and the pool is a real bonus."),
            turn("assistant", "Sounds like the right move."),
        ]),
    ],
    [0, 2])

# ── 2. as-of: dose at a past moment ──────────────────────────────────────────────────
s1 = d(2026, 2, 3, 9, 15)
s2 = d(2026, 2, 20, 20, 0)
s3 = d(2026, 4, 7, 8, 40)
appointment = s3 - timedelta(days=1)
entry(
    "tg-asof-002", "temporal-as-of",
    "How many allergy tablets was I taking each morning during March?",
    f"One tablet each morning. The dose only went up to two after the appointment on "
    f"{human(appointment)}, so throughout March you were still taking one.",
    d(2026, 5, 15, 11, 30),
    [
        (s1, [
            turn("user", "Started the new allergy tablets this morning — one tablet with breakfast.", True),
            turn("assistant", "Any side effects so far?"),
            turn("user", "A bit drowsy, otherwise fine."),
            turn("assistant", "Drowsiness usually settles after the first week or so."),
        ]),
        (s2, [
            turn("user", "Recommend me a film for a quiet Friday night?"),
            turn("assistant", "Something slow and warm — a road-trip drama, if you are in that mood."),
        ]),
        (s3, [
            turn("user", "The doctor doubled my allergy dose at yesterday's appointment — two tablets each morning now.", True),
            turn("assistant", "Did they say how long you would stay on the higher dose?"),
            turn("user", "Until the pollen drops off, so most of the summer."),
            turn("assistant", "Worth setting a reminder to review it after that."),
        ]),
    ],
    [0, 2])

# ── 3. as-of: the delivery date we were working to ───────────────────────────────────
kickoff = d(2026, 3, 2, 11, 0)
original_delivery = kickoff + timedelta(weeks=8)
pushed = d(2026, 3, 30, 16, 20)
s3 = d(2026, 4, 15, 12, 5)
entry(
    "tg-asof-003", "temporal-as-of",
    "What delivery date were we working to on the Harbour project before the client moved it?",
    f"{human(original_delivery)} — eight weeks from the kick-off on {human(kickoff)}. The three-week "
    f"slip the client asked for at the end of March pushed it later than that.",
    d(2026, 5, 20, 10, 0),
    [
        (kickoff, [
            turn("user", "Harbour project kicked off this morning. The client wants delivery eight weeks from today.", True),
            turn("assistant", "Eight weeks is tight for a project of that size. What is the biggest unknown?"),
            turn("user", "Their data export. Nobody has seen it yet."),
            turn("assistant", "Get a sample early — that is where the schedule usually goes."),
        ]),
        (pushed, [
            turn("user", "Client pushed Harbour delivery back by three weeks — the export was worse than anyone expected.", True),
            turn("assistant", "Called it. Does the extra time cover the cleanup?"),
            turn("user", "Just about."),
            turn("assistant", "Then take it and do not give the slack back."),
        ]),
        (s3, [
            turn("user", "Is it worth servicing a bike myself or paying a shop?"),
            turn("assistant", "Learn brakes and gears yourself; leave wheel truing and bearings to a shop."),
        ]),
    ],
    [0, 1])

# ── 4. as-of: how long had it been going ─────────────────────────────────────────────
physio_start = d(2026, 1, 20, 19, 0)
distractor = d(2026, 2, 2, 13, 15)
better = d(2026, 2, 17, 19, 5)
weeks_between = (better.date() - physio_start.date()).days / 7
entry(
    "tg-asof-004", "temporal-as-of",
    "How long had I been going to physio by the time I told you my shoulder felt better?",
    f"About four weeks — physio started on {human(physio_start)} and you said the shoulder felt "
    f"better on {human(better)}, which is {int((better.date() - physio_start.date()).days)} days later.",
    d(2026, 3, 10, 8, 30),
    [
        (physio_start, [
            turn("user", "First physio session for the shoulder tonight. Twice a week from here.", True),
            turn("assistant", "What did they have you doing?"),
            turn("user", "Band work and a lot of very slow raises."),
            turn("assistant", "Slow and boring is usually the part that works."),
        ]),
        (distractor, [
            turn("user", "What is a reasonable amount to spend on a first pair of running shoes?"),
            turn("assistant", "Mid-range and properly fitted beats expensive and guessed."),
        ]),
        (better, [
            turn("user", "Shoulder genuinely feels better tonight — first session where nothing pinched.", True),
            turn("assistant", "That is a good marker. Are you keeping the twice-a-week rhythm?"),
            turn("user", "For another month at least."),
            turn("assistant", "Sensible."),
        ]),
    ],
    [0, 2])

# ── 5. current: is the trial still running ───────────────────────────────────────────
trial_start = d(2026, 4, 28, 10, 0)
trial_end = trial_start + timedelta(days=30)
asked = d(2026, 6, 3, 9, 0)
entry(
    "tg-current-001", "temporal-current",
    "Is my Lumen trial still running?",
    f"No. The thirty-day trial started on {human(trial_start)} and ran out on {human(trial_end)}, "
    f"which is already past.",
    asked,
    [
        (trial_start, [
            turn("user", "Started the Lumen analytics free trial this morning — thirty days.", True),
            turn("assistant", "What are you hoping to get out of it?"),
            turn("user", "Mostly whether the funnel reports are worth the price."),
            turn("assistant", "Give yourself a checkpoint before the trial runs out so the decision is not rushed."),
        ]),
        (d(2026, 5, 6, 18, 40), [
            turn("user", "The cat has started sleeping on my keyboard. Any ideas?"),
            turn("assistant", "Give her a warmer, better spot right next to it — cats take the upgrade."),
        ]),
    ],
    [0])

# ── 6. current: have I started the new job ───────────────────────────────────────────
offer = d(2026, 2, 16, 21, 0)
start_day = first_weekday_of_next_month(offer, 0)
asked = d(2026, 4, 20, 9, 30)
weeks_in_post = (asked.date() - start_day.date()).days // 7
entry(
    "tg-current-002", "temporal-current",
    "Have I started at Halcyon Systems yet?",
    f"Yes. You started on {human(start_day)} — the first Monday of the month after you accepted — "
    f"so by now you have been there about {weeks_in_post} weeks.",
    asked,
    [
        (offer, [
            turn("user", "I accepted the Halcyon Systems offer tonight. I start the first Monday of next month.", True),
            turn("assistant", "Congratulations. How much notice did you have to give?"),
            turn("user", "Two weeks, which just about fits."),
            turn("assistant", "Then you get a short breather in between. Take it."),
        ]),
        (d(2026, 3, 9, 7, 55), [
            turn("user", "Is it rude to decline a meeting with no agenda?"),
            turn("assistant", "Not if you ask for the agenda first — most of them then quietly disappear."),
        ]),
    ],
    [0])

# ── 7. current: how old is the puppy now ─────────────────────────────────────────────
adopted = d(2026, 3, 15, 15, 20)
asked = d(2026, 6, 7, 11, 0)
weeks_since = (asked.date() - adopted.date()).days // 7
entry(
    "tg-current-003", "temporal-current",
    "How old is the puppy now?",
    f"About {8 + weeks_since} weeks old — she was eight weeks old when you brought her home on "
    f"{human(adopted)}, which was roughly {weeks_since} weeks ago.",
    asked,
    [
        (adopted, [
            turn("user", "We brought the puppy home today. She is eight weeks old and completely fearless.", True),
            turn("assistant", "Eight weeks is right at the start of the socialisation window — meet as many people as you can."),
            turn("user", "Noted. She has already met the whole street."),
            turn("assistant", "Then you are ahead."),
        ]),
        (d(2026, 4, 26, 20, 10), [
            turn("user", "Best way to get red wine out of a rug?"),
            turn("assistant", "Blot, never rub, then cold water and a little washing-up liquid."),
        ]),
    ],
    [0])

# ── 8. current: is the car service due ───────────────────────────────────────────────
serviced = d(2026, 1, 29, 16, 0)
next_service = add_months(serviced, 6)
asked = d(2026, 6, 20, 10, 15)
entry(
    "tg-current-004", "temporal-current",
    "Is my car service due yet?",
    f"Not yet. The car was serviced on {human(serviced)} and the interval is six months, so the "
    f"next one falls due around {human(next_service)} — still a few weeks away.",
    asked,
    [
        (serviced, [
            turn("user", "Car went in for its service this afternoon. They do it every six months.", True),
            turn("assistant", "Anything unexpected?"),
            turn("user", "Rear pads, which I half expected."),
            turn("assistant", "Better found on a service than on a hill."),
        ]),
        (d(2026, 3, 4, 19, 30), [
            turn("user", "Is it worth learning to make bread with a starter?"),
            turn("assistant", "Only if you enjoy the routine — the starter needs feeding whether you bake or not."),
        ]),
    ],
    [0])

# ── 9. prospective: not started yet ──────────────────────────────────────────────────
offer = d(2026, 5, 11, 20, 15)
start_day = first_weekday_of_next_month(offer, 0)
asked = d(2026, 5, 27, 9, 0)
days_away = (start_day.date() - asked.date()).days
entry(
    "tg-prospective-001", "prospective-memory",
    "Have I started at Meridian Health yet?",
    f"No, not yet. You start on {human(start_day)} — the first Monday of next month as of that "
    f"conversation — which is still about {days_away} days away.",
    asked,
    [
        (offer, [
            turn("user", "Signed the Meridian Health contract tonight. First Monday of next month is day one.", True),
            turn("assistant", "How are you feeling about it?"),
            turn("user", "Mostly relieved. It has been a long process."),
            turn("assistant", "Take the gap between jobs seriously — it does not come round often."),
        ]),
        (d(2026, 5, 19, 12, 40), [
            turn("user", "Quick one: does resting dough in the fridge overnight actually help?"),
            turn("assistant", "Yes — slower fermentation, better flavour, easier to shape cold."),
        ]),
    ],
    [0])

# ── 10. prospective: decision not due yet ────────────────────────────────────────────
applied = d(2026, 4, 14, 14, 50)
decision_due = applied + timedelta(weeks=8)
asked = d(2026, 5, 26, 8, 20)
weeks_left = round((decision_due.date() - asked.date()).days / 7)
entry(
    "tg-prospective-002", "prospective-memory",
    "Should I have heard back about the visa application by now?",
    f"No. They quoted eight weeks from the application on {human(applied)}, which puts the decision "
    f"around {human(decision_due)} — roughly {weeks_left} weeks after the date you are asking.",
    asked,
    [
        (applied, [
            turn("user", "Visa application went in today. They said to allow eight weeks for a decision.", True),
            turn("assistant", "Did they give you a reference to track it?"),
            turn("user", "Yes, and an email address that I suspect nobody reads."),
            turn("assistant", "Keep the reference somewhere you will find it in two months."),
        ]),
        (d(2026, 5, 2, 17, 5), [
            turn("user", "What is a good present for someone who says they want nothing?"),
            turn("assistant", "Something consumable and slightly too nice to buy for yourself."),
        ]),
    ],
    [0])

# ── 11. prospective: it has already happened ─────────────────────────────────────────
told = d(2026, 3, 5, 18, 0)
inspection = told + timedelta(days=1) + timedelta(weeks=2)
asked = d(2026, 4, 2, 9, 45)
entry(
    "tg-prospective-003", "prospective-memory",
    "Is the flat inspection still coming up?",
    f"No, it has already happened. A fortnight from the day after that conversation puts it on "
    f"{human(inspection)}, which is now in the past.",
    asked,
    [
        (told, [
            turn("user", "The letting agent booked the flat inspection for a fortnight from tomorrow.", True),
            turn("assistant", "Anything you need to fix before then?"),
            turn("user", "The bathroom extractor, mostly."),
            turn("assistant", "Worth doing — extractors are the first thing they look at."),
        ]),
        (d(2026, 3, 24, 21, 30), [
            turn("user", "Any tips for sleeping on a long flight?"),
            turn("assistant", "Aisle seat, no screens for the last hour, and accept that broken sleep is still sleep."),
        ]),
    ],
    [0])

# ── 12. prospective: eligibility not reached yet ─────────────────────────────────────
joined = d(2026, 2, 24, 19, 45)
eligible = add_months(joined, 6)
asked = d(2026, 6, 15, 13, 0)
entry(
    "tg-prospective-004", "prospective-memory",
    "Can I renew the season ticket at the members' rate yet?",
    f"Not yet. You joined the club on {human(joined)} and the members' rate needs six months of "
    f"membership, so you become eligible around {human(eligible)}.",
    asked,
    [
        (joined, [
            turn("user", "Joined the rowing club tonight. The members' rate on season tickets only kicks in after six months.", True),
            turn("assistant", "Is the season ticket much cheaper at that rate?"),
            turn("user", "About a third off, so worth waiting for."),
            turn("assistant", "Then plan the renewal around it rather than the other way round."),
        ]),
        (d(2026, 4, 8, 7, 20), [
            turn("user", "Does cold water actually make you more alert or is that a myth?"),
            turn("assistant", "The jolt is real but short — it is the routine around it that does the work."),
        ]),
    ],
    [0])


# ── verification ─────────────────────────────────────────────────────────────────────
problems = []
seen_ids = set()
for e in entries:
    if e["question_id"] in seen_ids:
        problems.append(f"duplicate id {e['question_id']}")
    seen_ids.add(e["question_id"])

    asked = datetime.strptime(re.sub(r"\s*\([^)]*\)", "", e["question_date"]), "%Y/%m/%d %H:%M")
    dates = [datetime.strptime(re.sub(r"\s*\([^)]*\)", "", s), "%Y/%m/%d %H:%M") for s in e["haystack_dates"]]
    for stamped, parsed in zip(e["haystack_dates"], dates):
        weekday = re.search(r"\((\w+)\)", stamped).group(1)
        if weekday != parsed.strftime("%a"):
            problems.append(f"{e['question_id']}: {stamped} says {weekday}, actual {parsed.strftime('%a')}")
    if any(when >= asked for when in dates):
        problems.append(f"{e['question_id']}: a session is not earlier than the question date")
    if sorted(dates) != dates:
        problems.append(f"{e['question_id']}: sessions are not in chronological order")
    if len(e["haystack_sessions"]) != len(e["haystack_dates"]) != len(e["haystack_session_ids"]):
        problems.append(f"{e['question_id']}: session/date/id counts disagree")
    if not e["answer_session_ids"]:
        problems.append(f"{e['question_id']}: no gold sessions")
    for gold in e["answer_session_ids"]:
        if gold not in e["haystack_session_ids"]:
            problems.append(f"{e['question_id']}: gold id {gold} is not in the haystack")
    for session in e["haystack_sessions"]:
        for t in session:
            if DATE_LIKE.search(t["content"]):
                problems.append(f"{e['question_id']}: absolute date in content: {t['content'][:60]}")
    if DATE_LIKE.search(e["question"]):
        problems.append(f"{e['question_id']}: absolute date in the question")
    gold_flagged = any(
        t.get("has_answer")
        for i, session in enumerate(e["haystack_sessions"])
        if e["haystack_session_ids"][i] in e["answer_session_ids"]
        for t in session)
    if not gold_flagged:
        problems.append(f"{e['question_id']}: no has_answer turn in a gold session")

by_type = {}
for e in entries:
    by_type[e["question_type"]] = by_type.get(e["question_type"], 0) + 1

print("entries:", len(entries))
print("by type:", by_type)
print("problems:", problems if problems else "none")
for e in entries:
    print(f"  {e['question_id']:24s} asked {e['question_date']}  ->  {e['answer'][:78]}")

if problems:
    raise SystemExit("corpus verification failed")

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(entries, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
print("written:", OUT)
