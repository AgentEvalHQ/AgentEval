# -*- coding: utf-8 -*-
"""No private path may be TRACKED, and the number of history commits that touched one may not grow.

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

WHAT IT CHECKS
--------------
1. **Nothing private is tracked right now.** `git ls-files` lists what is IN the repository, which is
   the question that matters -- not what `git status` shows, which hides ignored files by design.
   This is the check that would have failed the 2026-05-17 commit.

2. **The historical count has not grown.** Four commits touched `strategy/` and that number is
   declared below. It cannot go down without rewriting published history, so a ratchet is the honest
   shape: a NEW private commit raises it and fails, and the existing exposure stays visible in the
   failure message rather than being quietly normalised.

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


def git(*args):
    return subprocess.run(['git', *args],  # DevSkim: ignore DS107369 - fixed argv
                          cwd=ROOT, capture_output=True, text=True).stdout


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ablate-track', metavar='PATH',
                    help='pretend PATH is tracked, to prove the check can fail')
    args = ap.parse_args()

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
    if history > DECLARED_HISTORY_COMMITS:
        print('\nA NEW commit has touched a private path: %d against a declared %d.'
              % (history, DECLARED_HISTORY_COMMITS))
        print('Deleting the file in a follow-up commit does NOT undo this -- the blob stays')
        print('reachable by hash for anyone who clones. Deal with it now, while it is one commit.')
        return 1
    if history < DECLARED_HISTORY_COMMITS:
        print('\nThe count went DOWN (%d < %d), which only happens if published history was'
              % (history, DECLARED_HISTORY_COMMITS))
        print('rewritten. That is a real event and should be a deliberate edit to this file, not a')
        print('silent pass. Update DECLARED_HISTORY_COMMITS in the same commit as the rewrite.')
        return 1
    print('no private path is tracked, and the historical count has not grown')
    return 0


sys.exit(main())
