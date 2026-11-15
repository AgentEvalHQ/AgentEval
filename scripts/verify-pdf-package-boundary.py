#!/usr/bin/env python3
"""Verify the real distribution boundary after packing; no restore or publication is performed."""
import argparse
import json
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile

parser = argparse.ArgumentParser()
parser.add_argument("--feed", required=True, type=Path)
parser.add_argument("--base-assets", type=Path)
parser.add_argument("--provider-assets", type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]

def engine(name):
    return any(term in name.lower() for term in ("questpdf", "pdfsharp", "migradoc"))

def require_license_expression(specification, expected):
    license = specification.find(".//{*}license")
    assert license is not None and license.get("type") == "expression" and license.text == expected, expected

def require_file(package, logical_path, source):
    # NuGet may emit repeated separators. Reject aliases and compare the actual distributed legal text.
    matches = [name for name in package.namelist() if "/".join(part for part in name.split("/") if part) == logical_path]
    assert len(matches) == 1, (logical_path, matches)
    assert package.read(matches[0]) == (root / source).read_bytes(), (logical_path, "contents differ from source")

packages = {}
for path in args.feed.glob("*.nupkg"):
    with ZipFile(path) as package:
        nuspecs = [name for name in package.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise SystemExit(f"Expected one nuspec: {path}")
        specification = ET.fromstring(package.read(nuspecs[0]))
        identity = specification.find(".//{*}id").text
        if identity in packages:
            raise SystemExit(f"Ambiguous package versions in feed for {identity}")
        packages[identity] = (path, specification)

for identity in ("AgentEval", "AgentEval.Cli"):
    path, specification = packages[identity]
    dependencies = [node.attrib["id"] for node in specification.findall(".//{*}dependency")]
    assert not any(engine(name) for name in dependencies), (identity, dependencies)
    with ZipFile(path) as package:
        forbidden = [name for name in package.namelist() if engine(name) or "latofont/" in name.lower() or name.lower().endswith("lato-regular.ttf")]
        assert not forbidden, (identity, forbidden)
        for name in package.namelist():
            if name.endswith(".deps.json"):
                libraries = json.loads(package.read(name)).get("libraries", {})
                assert not any(engine(key) for key in libraries), (identity, name)
    print(f"PASS {identity}: no PDF engine or exclusive font assets")

path, specification = packages["AgentEval.Rendering.PdfSharp"]
dependencies = {node.attrib["id"].lower(): node.attrib["version"] for node in specification.findall(".//{*}dependency")}
assert "agenteval" in dependencies, dependencies
assert not any(name.startswith("agenteval.") for name in dependencies), dependencies
groups = specification.findall(".//{*}dependencies/{*}group")
assert groups, "Expected provider dependency groups"
for group in groups:
    declared = {node.attrib["id"].lower(): node.attrib["version"] for node in group.findall("{*}dependency")}
    assert declared.get("pdfsharp-migradoc") == "[6.2.4]" and declared.get("pdfsharp") == "[6.2.4]", (group.attrib, declared)
require_license_expression(specification, "MIT AND OFL-1.1")
with ZipFile(path) as package:
    require_file(package, "licenses/Lato/OFL.txt", "src/AgentEval.Rendering.PdfSharp/Fonts/OFL.txt")
    require_file(package, "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md")
    embedded = [name for name in package.namelist() if name.startswith("lib/") and name.endswith(".dll")]
    assert all(name.endswith("/AgentEval.Rendering.PdfSharp.dll") for name in embedded), embedded
print("PASS optional provider: normal base dependency, exact reviewed engine version, no duplicated shared assemblies, font license")

path, specification = packages["AgentEval.Cli.Pdf"]
require_license_expression(specification, "MIT AND Apache-2.0 AND OFL-1.1")
with ZipFile(path) as package:
    entries = package.namelist()
    assert any(name.endswith("/AgentEval.Rendering.PdfSharp.dll") for name in entries)
    assert any(name.endswith("/PdfSharp.dll") for name in entries)
    for required, source in (("licenses/Lato/OFL.txt", "src/AgentEval.Rendering.PdfSharp/Fonts/OFL.txt"),
                             ("licenses/aef/LICENSE", "contracts/aef/LICENSE"), ("licenses/aef/NOTICE", "contracts/aef/NOTICE"),
                             ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md")):
        require_file(package, required, source)
    assert not any("questpdf" in name.lower() for name in entries)
    manifests = [name for name in entries if name.endswith(".deps.json")]
    assert manifests, "Expected companion runtime dependency manifests"
    for manifest in manifests:
        libraries = {key.lower() for key in json.loads(package.read(manifest)).get("libraries", {})}
        assert "pdfsharp/6.2.4" in libraries and "pdfsharp-migradoc/6.2.4" in libraries, (manifest, libraries)
print("PASS optional companion: provider and required notices present, QuestPDF absent")

if args.base_assets:
    libraries = json.loads(args.base_assets.read_text(encoding="utf-8"))["libraries"]
    assert not any(engine(key) for key in libraries), libraries
    print("PASS fresh base consumer: restored graph is engine-free")
if args.provider_assets:
    libraries = {key.lower(): value for key, value in json.loads(args.provider_assets.read_text(encoding="utf-8"))["libraries"].items()}
    assert "pdfsharp-migradoc/6.2.4" in libraries and "pdfsharp/6.2.4" in libraries, libraries.keys()
    assert not any("questpdf" in key.lower() for key in libraries), libraries.keys()
    assert not any(key.startswith(("agenteval.core/", "agenteval.abstractions/", "agenteval.rendering.pdf/")) for key in libraries), libraries.keys()
    print("PASS fresh provider consumer: usable base dependency and reviewed renderer versions")
