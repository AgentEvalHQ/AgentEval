"""Archive-level regressions for the PDF release guard; no build, restore or network access."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[2]
GUARD = ROOT / "scripts/verify-pdf-package-boundary.py"


class PdfPackageBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="pdf-package-guard-")
        self.addCleanup(self.temporary.cleanup)
        self.feed = Path(self.temporary.name)
        self.packages = {}
        for identity in ("AgentEval", "AgentEval.Cli"):
            self.packages[identity] = {identity + ".nuspec": self.nuspec(identity, "MIT")}
        provider = "AgentEval.Rendering.PdfSharp"
        deps = '<dependency id="AgentEval" version="1.0.0" /><dependency id="PDFsharp" version="[6.2.4]" /><dependency id="PdfSharp-MigraDoc" version="[6.2.4]" />'
        groups = ''.join(f'<group targetFramework="{tfm}">{deps}</group>' for tfm in ("net8.0", "net9.0", "net10.0"))
        self.packages[provider] = {
            provider + ".nuspec": self.nuspec(provider, "MIT AND OFL-1.1", groups),
            "lib/net8.0/AgentEval.Rendering.PdfSharp.dll": b"fixture assembly",
            "licenses/Lato/OFL.txt": (ROOT / "src/AgentEval.Rendering.PdfSharp/Fonts/OFL.txt").read_bytes(),
            "THIRD-PARTY-NOTICES.md": (ROOT / "THIRD-PARTY-NOTICES.md").read_bytes(),
        }
        companion = "AgentEval.Cli.Pdf"
        self.packages[companion] = {
            companion + ".nuspec": self.nuspec(companion, "MIT AND Apache-2.0 AND OFL-1.1"),
            "tools/net8.0/any/AgentEval.Rendering.PdfSharp.dll": b"fixture provider",
            "tools/net8.0/any/PdfSharp.dll": b"fixture engine",
            "tools/net8.0/any/AgentEval.Cli.Pdf.deps.json": json.dumps({"libraries": {"PDFsharp/6.2.4": {}, "PdfSharp-MigraDoc/6.2.4": {}}}).encode(),
            "licenses/Lato/OFL.txt": (ROOT / "src/AgentEval.Rendering.PdfSharp/Fonts/OFL.txt").read_bytes(),
            "licenses/aef/LICENSE": (ROOT / "contracts/aef/LICENSE").read_bytes(),
            "licenses/aef/NOTICE": (ROOT / "contracts/aef/NOTICE").read_bytes(),
            "THIRD-PARTY-NOTICES.md": (ROOT / "THIRD-PARTY-NOTICES.md").read_bytes(),
        }

    @staticmethod
    def nuspec(identity, license, groups=""):
        return (f'<package><metadata><id>{identity}</id><version>1.0.0</version>'
                f'<license type="expression">{license}</license><dependencies>{groups}</dependencies></metadata></package>').encode()

    def run_guard(self):
        for identity, entries in self.packages.items():
            with ZipFile(self.feed / (identity + ".1.0.0.nupkg"), "w") as package:
                for name, data in entries.items():
                    package.writestr(name, data)
        return subprocess.run([sys.executable, str(GUARD), "--feed", str(self.feed)], capture_output=True, text=True)

    def test_valid_distribution_passes(self):
        result = self.run_guard()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_direct_engine_upgrade_in_one_framework_is_rejected(self):
        package = self.packages["AgentEval.Rendering.PdfSharp"]
        key = "AgentEval.Rendering.PdfSharp.nuspec"
        package[key] = package[key].replace(b'id="PDFsharp" version="[6.2.4]"', b'id="PDFsharp" version="[6.5.0]"', 1)
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_unbounded_migradoc_version_is_rejected(self):
        package = self.packages["AgentEval.Rendering.PdfSharp"]
        key = "AgentEval.Rendering.PdfSharp.nuspec"
        package[key] = package[key].replace(b'id="PdfSharp-MigraDoc" version="[6.2.4]"', b'id="PdfSharp-MigraDoc" version="6.2.4"')
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_optional_license_expression_cannot_omit_distributed_terms(self):
        for identity in ("AgentEval.Rendering.PdfSharp", "AgentEval.Cli.Pdf"):
            with self.subTest(package=identity):
                key = identity + ".nuspec"
                original = self.packages[identity][key]
                self.packages[identity][key] = original.replace(b" AND OFL-1.1", b"")
                self.assertNotEqual(0, self.run_guard().returncode)
                self.packages[identity][key] = original

    def test_font_license_must_contain_the_original_text_in_both_packages(self):
        for identity in ("AgentEval.Rendering.PdfSharp", "AgentEval.Cli.Pdf"):
            with self.subTest(package=identity):
                original = self.packages[identity]["licenses/Lato/OFL.txt"]
                self.packages[identity]["licenses/Lato/OFL.txt"] = b"placeholder"
                self.assertNotEqual(0, self.run_guard().returncode)
                self.packages[identity]["licenses/Lato/OFL.txt"] = original

    def test_duplicate_logical_license_paths_are_rejected(self):
        package = self.packages["AgentEval.Rendering.PdfSharp"]
        package["licenses//Lato/OFL.txt"] = package["licenses/Lato/OFL.txt"]
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_aef_notice_must_match_source(self):
        self.packages["AgentEval.Cli.Pdf"]["licenses/aef/NOTICE"] = b"stale notice"
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_companion_dependency_manifest_cannot_use_a_different_engine(self):
        package = self.packages["AgentEval.Cli.Pdf"]
        key = "tools/net8.0/any/AgentEval.Cli.Pdf.deps.json"
        package[key] = package[key].replace(b"PDFsharp/6.2.4", b"PDFsharp/6.5.0")
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_default_package_cannot_embed_the_engine(self):
        self.packages["AgentEval"]["lib/net8.0/PdfSharp.dll"] = b"unexpected engine"
        self.assertNotEqual(0, self.run_guard().returncode)


if __name__ == "__main__":
    unittest.main()
