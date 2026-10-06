# -*- coding: utf-8 -*-
"""No private path may be TRACKED, the number of history commits that touched one may not grow, and
no tracked file may CITE one.

WHY THIS EXISTS, AND WHY .gitignore IS NOT ENOUGH
-------------------------------------------------
`strategy/` has been in `.gitignore` since 2026-01-07 and the repository is public. On 2026-05-17,
five files under `strategy/FutureFeatures/` were committed anyway -- `.gitignore` was at line 428 of
that very commit's own copy of the file. An ignore rule cannot stop `git add -f`, and it does not
apply to a path that is already tracked. Those are exactly the two ways private content reaches a
public remote, and neither is something `.gitignore` was ever able to prevent.

They were deleted a week and a month later. **Deleting a file does not remove it from history**: the
blobs stay reachable by commit hash for anyone who clones. So the deletion fixed the tree and not
the exposure, and nothing told anyone either way.

WHY A MENTION IS A LEAK TOO
---------------------------
On 2026-10-02 nothing under `strategy/` was tracked, and still 60 tracked files carried 139 lines
naming a path under it. Apart from the few files whose job is to name the prefix, every one was a
citation: ADRs deferring to plans no reader can open, XML doc comments sending the reader to design
documents that do not exist on the remote, and six clickable links that cannot resolve there. A
citation of a private path fails three ways at once. It is a dead link for every reader of this
public repository. It publishes the names and layout of private documents, and a file name is
content. And it asks the reader to trust a claim whose source they cannot check, which is the
opposite of what a citation is for. The first two checks could not see any of it, because they look
at paths and history, never at what a tracked file SAYS.

WHAT IT CHECKS
--------------
1. **Nothing private is tracked right now.** `git ls-files` lists what is IN the repository, which is
   the question that matters -- not what `git status` shows, which hides ignored files by design.
   This is the check that would have failed the 2026-05-17 commit.

2. **The historical count has not grown.** Four commits touched `strategy/` and that number is
   declared below. It cannot go down without rewriting published history, so a ratchet is the honest
   shape: a NEW private commit raises it and fails, and the existing exposure stays visible in the
   failure message rather than being quietly normalised.

3. **No tracked file mentions a private prefix, outside a short allowlist.** `git grep` reads every
   tracked file for the prefix text and its backslash spelling. The allowlist is the files whose job
   it is to name the prefix -- the ignore rules, the secret-scan exclusion, the two documents that
   state the rule, and the gate scripts -- each with its reason. **Every allowlisted file must still
   mention a prefix**: an exemption nobody uses is removed, not kept, so the list cannot quietly
   become a place to park the next citation. That requirement is also the positive control: a scan
   that finds nothing in `.gitignore`, whose rule IS the prefix, measured nothing.

   What it cannot see: the match is literal and case-sensitive, so a path assembled from segments
   (`os.path.join(ROOT, 'strategy', ...)`, which `check_tag_has_status_entry.py` does on purpose) or
   a differently-cased spelling passes. Describing a private document in words ("a local plan, not
   in this repository") also passes, and that is the intended fix, not a gap. A matching binary file
   is reported by name only.

The second check is why this is not just a pre-commit hook. A hook runs on the machine that has it
installed; this runs on every push, for everyone, including the force-add that nobody reviewed.
"""
import argparse
import os
import subprocess  # DevSkim: ignore DS107369 - reads this repository's own index and log
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

#: Path prefixes that must never be tracked in a public repository.
PRIVATE_PREFIXES = ('strategy/',)

#: Commits in the FULL history that touch a private prefix. A ratchet, not a target: it may not
#: grow. The four are 201a92c8 and c9ee7e77 (2026-05-17, which added five files under
#: strategy/FutureFeatures/) and 395237ac and 120561f6 (2026-05-25 and 2026-06-13, which deleted
#: them again). Lowering this number means rewriting published history, which is a decision for the
#: maintainer and not something a gate should invite.
DECLARED_HISTORY_COMMITS = 4

#: The only tracked files that may MENTION a private prefix, each because naming it is the file's
#: job. Every entry must still mention one (check 3): a stale exemption fails rather than lingering.
#: Adding a file here is a reviewed decision with a reason, never a way to make a citation pass.
MENTION_ALLOWLIST = {
    '.gitignore': 'the ignore rule itself',
    '.dockerignore': 'keeps the private tree out of a Docker build context',
    '.github/workflows/security.yml': 'excludes the private tree from the DevSkim scan, and says why',
    '.github/agents/agenteval-docwriter.agent.md': 'states the rule against citing private paths',
    '.github/instructions/documentation.instructions.md': 'states the rule against citing private paths',
    'tools/check_no_private_paths.py': 'this gate',
    'tools/check_tag_has_status_entry.py': 'reads the status doc, which lives under the prefix by design',
}


def git(*args):
    return subprocess.run(['git', *args],  # DevSkim: ignore DS107369 - fixed argv
                          cwd=ROOT, capture_output=True, text=True, encoding="utf-8",
                          errors="replace").stdout


def git_rc(*args):
    done = subprocess.run(['git', *args],  # DevSkim: ignore DS107369 - fixed argv
                          cwd=ROOT, capture_output=True, text=True, encoding="utf-8",
                          errors="replace")
    return done.returncode, done.stdout


def mention_patterns():
    """Each private prefix as written, plus its Windows spelling (`strategy\\`)."""
    patterns = []
    for prefix in PRIVATE_PREFIXES:
        patterns.append(prefix)
        if '/' in prefix:
            patterns.append(prefix.replace('/', '\\'))
    return patterns


def find_mentions():
    """Every tracked file whose content mentions a private prefix.

    Returns (lines, binary): `lines` maps a path to its [(line number, text)] for text files, and
    `binary` lists the matching files git treats as binary, which have no lines to show. Returns None
    when git grep itself failed (exit code above 1; 1 only means "no match").
    """
    args = []
    for pattern in mention_patterns():
        args += ['-e', pattern]
    rc_text, text_out = git_rc('grep', '--no-color', '-I', '-n', '-z', '-F', *args)
    rc_all, all_out = git_rc('grep', '--no-color', '-l', '-z', '-F', *args)
    if rc_text > 1 or rc_all > 1:
        return None
    lines = {}
    for record in text_out.split('\n'):
        if not record:
            continue
        parts = record.split('\0', 2)
        if len(parts) != 3 or not parts[1].isdigit():
            # Not a shape this parser knows. Treat it as a failed read, never as "no mention".
            print('CANNOT PARSE git grep output: %r' % record[:200])
            return None
        path, number, content = parts
        lines.setdefault(path, []).append((int(number), content.rstrip('\r')))
    binary = sorted(p for p in all_out.split('\0') if p and p not in lines)
    return lines, binary


def check_mentions(ablate):
    """Check 3. Returns 0 (pass), 1 (a citation or a stale exemption) or 2 (measured nothing)."""
    allow = dict(MENTION_ALLOWLIST)
    if ablate:
        print('ABLATION: dropping %s from the mention allowlist; its real mentions must now fail'
              % ablate)
        del allow[ablate]

    found = find_mentions()
    if found is None:
        print('CANNOT CHECK MENTIONS: git grep failed, so this measured nothing')
        return 2
    lines, binary = found
    matched = set(lines) | set(binary)

    # POSITIVE CONTROL. "no file mentions a private prefix" is also true of a grep that read nothing,
    # and .gitignore cannot pass this check honestly without naming the prefix it ignores.
    if not matched:
        print('CANNOT CHECK MENTIONS: git grep found no mention anywhere -- not even in .gitignore,')
        print('whose rule is the prefix itself -- so this measured nothing')
        return 2

    offenders = sorted(p for p in matched if p not in allow)
    stale = sorted(p for p in allow if p not in matched)
    count = sum(len(lines.get(p, [])) for p in offenders)
    print('Mentions of %s: %d tracked files, %d of them allowlisted; %d files outside it.'
          % (' / '.join(mention_patterns()), len(matched), len(matched) - len(offenders),
             len(offenders)))

    rc = 0
    if offenders:
        print('\nTRACKED FILES CITE A PRIVATE PATH (%d files, %d lines):' % (len(offenders), count))
        for path in offenders:
            if path in binary:
                print('  %s: (binary file; no line to show)' % path)
                continue
            for number, content in lines[path]:
                text = content.strip()
                print('  %s:%d: %s' % (path, number, text if len(text) <= 120 else text[:117] + '...'))
        print('\nEvery tracked file is public. A private path in one is a dead link for every reader,')
        print('it publishes the names of private documents, and it cites a source nobody can check.')
        print('Drop the pointer, or point to the PUBLIC document that states the same fact. Only a')
        print('file whose job is to name the prefix belongs in MENTION_ALLOWLIST, with its reason.')
        rc = 1
    if stale:
        print('\nALLOWLISTED, BUT NO LONGER MENTIONS A PRIVATE PREFIX:')
        for path in stale:
            print('  - %s (%s)' % (path, allow[path]))
        print('\nRemove the entry. An exemption nobody uses is where the next citation gets parked.')
        rc = 1
    return rc


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ablate-track', metavar='PATH',
                    help='pretend PATH is tracked, to prove the check can fail')
    ap.add_argument('--ablate-mention', metavar='FILE',
                    help='drop FILE from the mention allowlist, to prove the mention check can fail '
                         'on that file\'s REAL content. FILE must be on the allowlist, and every '
                         'entry must mention a prefix, so this always has something to catch.')
    args = ap.parse_args()

    ablate_mention = None
    if args.ablate_mention:
        ablate_mention = args.ablate_mention.replace('\\', '/')
        if ablate_mention.startswith('./'):
            ablate_mention = ablate_mention[2:]
        if ablate_mention not in MENTION_ALLOWLIST:
            print('ABLATION NOT APPLIED: %s is not on the mention allowlist, so dropping it would'
                  % ablate_mention)
            print('change nothing. Choose one of: %s' % ', '.join(sorted(MENTION_ALLOWLIST)))
            return 2

    tracked = [line.strip() for line in git('ls-files').splitlines() if line.strip()]

    # POSITIVE CONTROL. "nothing private is tracked" is also true of a listing that returned nothing,
    # which is what a wrong working directory or a broken git invocation produces.
    if len(tracked) < 100:
        print('CANNOT CHECK: git ls-files returned %d paths, so this measured nothing'
              % len(tracked))
        return 2
    if args.ablate_track:
        print('ABLATION: %d paths tracked; injecting %s' % (len(tracked), args.ablate_track))
        tracked.append(args.ablate_track)

    leaked = sorted(p for p in tracked if p.startswith(PRIVATE_PREFIXES))
    if leaked:
        print('PRIVATE PATHS ARE TRACKED IN A PUBLIC REPOSITORY:')
        for p in leaked:
            print('  - %s' % p)
        print('\nThese are IN the repository, not merely present on disk. .gitignore does not apply')
        print('to an already-tracked path and cannot stop `git add -f`, which is how five files')
        print('under strategy/ reached this remote on 2026-05-17 with the rule already in place.')
        print('Remove them from the index (git rm --cached) before this can pass.')
        return 1

    history = len([l for l in git('log', '--all', '--format=%h', '--', *PRIVATE_PREFIXES).splitlines()
                   if l.strip()])
    print('%d paths tracked, 0 private. History commits touching %s: %d (declared %d).'
          % (len(tracked), '/'.join(PRIVATE_PREFIXES), history, DECLARED_HISTORY_COMMITS))
    history_rc = 0
    if history > DECLARED_HISTORY_COMMITS:
        print('\nA NEW commit has touched a private path: %d against a declared %d.'
              % (history, DECLARED_HISTORY_COMMITS))
        print('Deleting the file in a follow-up commit does NOT undo this -- the blob stays')
        print('reachable by hash for anyone who clones. Deal with it now, while it is one commit.')
        history_rc = 1
    elif history < DECLARED_HISTORY_COMMITS:
        print('\nThe count went DOWN (%d < %d), which only happens if published history was'
              % (history, DECLARED_HISTORY_COMMITS))
        print('rewritten. That is a real event and should be a deliberate edit to this file, not a')
        print('silent pass. Update DECLARED_HISTORY_COMMITS in the same commit as the rewrite.')
        history_rc = 1

    # Check 3 runs even when check 2 failed, so one CI run reports every problem at once.
    mention_rc = check_mentions(ablate_mention)
    if history_rc == 1 or mention_rc == 1:
        return 1
    if mention_rc:
        return mention_rc
    print('no private path is tracked, the historical count has not grown, and no tracked file')
    print('outside the allowlist mentions one')
    return 0


sys.exit(main())
