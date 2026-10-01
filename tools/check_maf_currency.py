#!/usr/bin/env python3
"""Fail when the Microsoft Agent Framework stack pinned in Directory.Packages.props falls behind NuGet.

Why this exists: Dependabot's NuGet version updates never completed on this repository. Every weekly
run from June to September 2026 spent ~55 minutes failing dependency discovery (NETSDK1005 on the
multi-targeted projects) and was cancelled, and after 2026-09-06 none ran at all. MAF meanwhile moved
from 1.17 to 1.23 and nothing said so. This check reads the pins and NuGet's flat-container index
directly, so it cannot be silently skipped the same way: it either prints a verdict or fails.

Usage:
  python tools/check_maf_currency.py                 # check the repo's Directory.Packages.props
  python tools/check_maf_currency.py --self-test     # exercise the verdict logic offline, no network
  python tools/check_maf_currency.py --max-minor-lag 1

Exit codes: 0 = current (within the allowed lag), 1 = behind, 2 = could not measure (network or parse
failure). "Could not measure" is never reported as current.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.request
from pathlib import Path

# Every STABLE package of the stack gates the check: a stale OpenAI, Harness or Generators pin is as real a lag
# as a stale core. Preview-only packages (Foundry, A2A) follow the stable core's version line and are reported.
GATED = (
    "Microsoft.Agents.AI",
    "Microsoft.Agents.AI.OpenAI",
    "Microsoft.Agents.AI.Workflows",
    "Microsoft.Agents.AI.Workflows.Generators",
    "Microsoft.Agents.AI.Harness",
    "Microsoft.Extensions.AI",
    "Microsoft.Extensions.AI.OpenAI",
    "Microsoft.Extensions.AI.Evaluation.Quality",
)
REPORTED = (
    "Microsoft.Agents.AI.Foundry",
    "Microsoft.Agents.AI.A2A",
)

REPO_ROOT = Path(__file__).resolve().parent.parent

PIN = re.compile(r'<PackageVersion\s+Include="(?P<id>[^"]+)"\s+Version="(?P<ver>[^"]+)"')


def parse_pins(props_text: str) -> dict[str, str]:
    return {m.group("id"): m.group("ver") for m in PIN.finditer(props_text)}


def parse_version(v: str) -> tuple[int, int, int, bool]:
    core, _, pre = v.partition("-")
    parts = (core.split(".") + ["0", "0"])[:3]
    return int(parts[0]), int(parts[1]), int(parts[2]), bool(pre)


def latest_from_index(versions: list[str], prerelease: bool) -> str | None:
    pool = [v for v in versions if prerelease or "-" not in v]
    if not pool:
        return None
    return max(pool, key=lambda v: (parse_version(v)[:3], not parse_version(v)[3], v))


def minor_lag(pinned: str, latest: str) -> int:
    pm, pn, _, _ = parse_version(pinned)
    lm, ln, _, _ = parse_version(latest)
    if lm != pm:
        return 1000 * (lm - pm) + ln  # a major behind is always "behind"
    return ln - pn


def fetch_versions(package_id: str) -> list[str]:
    url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"
    with urllib.request.urlopen(url, timeout=30) as resp:  # noqa: S310 - fixed https host
        return json.load(resp)["versions"]


def evaluate(pins: dict[str, str], index: dict[str, list[str]], max_lag: int) -> tuple[int, list[str]]:
    """Return (exit_code, report_lines). Pure: no I/O, so --self-test exercises the real logic."""
    lines: list[str] = []
    behind = False
    for pkg in GATED + REPORTED:
        pinned = pins.get(pkg)
        if pinned is None:
            if pkg in GATED:
                # A gated pin that is missing is a parser miss or a removal, never "current".
                return 2, lines + [f"COULD NOT MEASURE: gated package {pkg} is not pinned in Directory.Packages.props"]
            lines.append(f"  {pkg}: not pinned in Directory.Packages.props (skipped)")
            continue
        versions = index.get(pkg)
        if not versions:
            return 2, lines + [f"COULD NOT MEASURE: no NuGet versions for {pkg}"]
        latest = latest_from_index(versions, prerelease="-" in pinned)
        if latest is None:
            return 2, lines + [f"COULD NOT MEASURE: no comparable NuGet version for {pkg}"]
        lag = minor_lag(pinned, latest)
        gated = pkg in GATED
        flag = ""
        if lag > max_lag and gated:
            behind = True
            flag = f"  <-- BEHIND by {lag} minor (allowed {max_lag})"
        elif lag > 0:
            flag = f"  (behind by {lag} minor{'' if gated else ', reported only'})"
        lines.append(f"  {pkg}: pinned {pinned}, latest {latest}{flag}")
    return (1 if behind else 0), lines


def self_test() -> int:
    current = {p: "1.23.0" for p in GATED}
    current["Microsoft.Extensions.AI"] = "10.10.0"
    current["Microsoft.Extensions.AI.OpenAI"] = "10.10.1"
    pins = {**current, "Microsoft.Agents.AI": "1.17.0", "Microsoft.Agents.AI.Foundry": "1.17.0-preview.1"}
    idx = {p: [v] for p, v in current.items()}
    idx["Microsoft.Agents.AI"] = ["1.17.0", "1.22.0", "1.23.0", "1.24.0-preview.1"]
    idx["Microsoft.Extensions.AI"] = ["10.9.0", "10.10.0"]
    idx["Microsoft.Agents.AI.Foundry"] = ["1.17.0-preview.1", "1.23.0-preview.2"]
    checks = []
    code, _ = evaluate(pins, idx, max_lag=1)
    checks.append(("6 minors behind fails", code == 1))
    code, _ = evaluate({**pins, "Microsoft.Agents.AI": "1.22.0"}, idx, max_lag=1)
    checks.append(("1 minor behind passes at lag 1", code == 0))
    code, _ = evaluate({**pins, "Microsoft.Agents.AI": "1.22.0"}, idx, max_lag=0)
    checks.append(("1 minor behind fails at lag 0", code == 1))
    code, _ = evaluate(pins, {k: v for k, v in idx.items() if k != "Microsoft.Extensions.AI"}, max_lag=1)
    checks.append(("missing index is 'could not measure', never current", code == 2))
    checks.append(("prerelease is ignored for a stable pin", latest_from_index(idx["Microsoft.Agents.AI"], False) == "1.23.0"))
    checks.append(("preview pin compares against previews", latest_from_index(idx["Microsoft.Agents.AI.Foundry"], True) == "1.23.0-preview.2"))
    checks.append(("parser reads a real pin line", parse_pins('<PackageVersion Include="A.B" Version="1.2.3" />') == {"A.B": "1.2.3"}))
    code, _ = evaluate({}, {}, max_lag=1)
    checks.append(("no gated pins at all is 'could not measure', never current", code == 2))
    failed = [name for name, ok in checks if not ok]
    for name, ok in checks:
        print(f"  [{'ok' if ok else 'FAIL'}] {name}")
    return 1 if failed else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--props", default=str(REPO_ROOT / "Directory.Packages.props"))
    ap.add_argument("--max-minor-lag", type=int, default=1)
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    try:
        pins = parse_pins(Path(args.props).read_text(encoding="utf-8"))
        index = {p: fetch_versions(p) for p in GATED + REPORTED if p in pins}
    except Exception as exc:  # network, parse: never "current"
        print(f"COULD NOT MEASURE: {type(exc).__name__}: {exc}")
        return 2
    try:
        code, lines = evaluate(pins, index, args.max_minor_lag)
    except ValueError as exc:  # an unparseable version string: never "behind", never "current"
        print(f"COULD NOT MEASURE: unparseable version: {exc}")
        return 2
    print("MAF currency check:")
    print("\n".join(lines))
    print({0: "VERDICT: current", 1: "VERDICT: BEHIND - bump Directory.Packages.props", 2: "VERDICT: could not measure"}[code])
    return code


if __name__ == "__main__":
    sys.exit(main())
