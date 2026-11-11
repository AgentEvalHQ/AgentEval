# AgentEval optional PDF companion

Source/local-package usage (publication follows the normal release process):

```powershell
dotnet tool install --global AgentEval.Cli.Pdf --version "<matching-package-version>" --add-source ./nupkgs
agenteval-pdf render --run .agenteval/aef/runs/<year>/<month>/<run-id> --output ./report.pdf
```

Replace `<matching-package-version>` with the exact version in the local feed, including any prerelease suffix. Keep it aligned with the AgentEval/provider version you use. The explicit version selects that prerelease; an unversioned install searches for stable releases by default. `--global` makes the command available through your user's tool directory. These local packages are not claimed to be published until the release process publishes them.

This optional tool installs and explicitly registers the MIT PDFsharp/MigraDoc **6.2.4** renderer. The stock `agenteval` CLI remains engine-free. The render command verifies existing AEF evidence, requires captured content for rich GDPR/EU AI Act/agentic reports, and never invokes an agent or judge. `--family generic` explicitly requests a summary of recorded AEF fields. It does not substitute a generic summary for a missing rich report.

Automatic family selection reads verified suite/report descriptors and compliance artifact identities, including imported reports without a suite. If the family is unknown or several rich families are present, select `--family` explicitly; the tool never silently switches to a generic report.

Output must be outside the sealed input run. An existing destination requires `--overwrite`. Existing captured PDF evidence can still be read or downloaded using the engine-free base readers.
