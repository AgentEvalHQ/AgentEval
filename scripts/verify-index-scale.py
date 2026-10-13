#!/usr/bin/env python3
"""Run deterministic offline index scale checks in a disposable directory."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--fixture", required=True, type=Path)
parser.add_argument("--sources", type=int, default=1000)
parser.add_argument("--results-per-source", type=int, default=20)
args = parser.parse_args()
if not args.fixture.is_file():
    parser.error("fixture must be a built IndexRebuildSmoke.dll")
if not 1 <= args.sources <= 5000 or not 1 <= args.results_per_source <= 100:
    parser.error("sources must be 1..5000 and results-per-source 1..100")
with tempfile.TemporaryDirectory(prefix="ae-index-scale-") as directory:
    result = subprocess.run(
        ["dotnet", str(args.fixture.resolve()), "scale", str(Path(directory) / "index.sqlite"),
         str(args.sources), str(args.results_per_source)],
        check=True, stdout=subprocess.PIPE, text=True, timeout=900)
    print(json.dumps(json.loads(result.stdout), indent=2))
