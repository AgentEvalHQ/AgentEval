// summarize.csx — computes the exact overage of a submitted meal amount against the corporate
// dinner cap ($150, per resources/policy.md).
//
// Expected `arguments` shape (a JSON object passed via the `run_skill_script` tool call):
//   { "amount": <number> }
//
// NOTE (sample-only, documented — see Program.cs `InProcessScriptRunner`): MAF's Agent Skills GA
// ships NO default subprocess script runner (verified against the live 1.13.0 assembly this
// session — there is no `SubprocessScriptRunner` type; a caller MUST supply an
// `AgentFileSkillScriptRunner` delegate for `run_skill_script` to do anything at all). Rather than
// shelling out to a `dotnet-script`/`csi` interpreter that may not be installed on every dev
// machine or CI runner, this sample registers an IN-PROCESS delegate in Program.cs that implements
// EXACTLY the logic below. The .csx file is kept as the human-readable "source of truth" for what
// the script does — this is a legitimate MAF-native authoring pattern (in-process script
// execution), not a scripted-fake stand-in for the agent or the model.

const double DinnerCap = 150.0;

double amount = Args.Amount;
double overage = Math.Max(0, amount - DinnerCap);

Console.WriteLine(overage > 0
    ? $"Meal amount ${amount:F2} vs policy cap ${DinnerCap:F2} -> OVER by ${overage:F2}."
    : $"Meal amount ${amount:F2} vs policy cap ${DinnerCap:F2} -> within policy.");
