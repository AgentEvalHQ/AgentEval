import { Sparkles, Terminal, ExternalLink, FolderOpen } from "lucide-react";

// Plan-08 MC1.10.1: first-run landing. Rendered in <AppShell/> when
// `Query.workspace { initialized }` is false — i.e. the user pointed
// `agenteval mc serve` at a directory that hasn't been initialised yet.
//
// Replaces the "empty dashboard" UX where every query returned null/[]
// and the user had no idea what to do next.

interface Props {
  root: string | null;
  agentEvalVersion: string;
}

export function WorkspaceLandingPage({ root, agentEvalVersion }: Props) {
  return (
    <div className="max-w-3xl mx-auto py-12 space-y-8">
      <header className="text-center space-y-3">
        <div className="inline-flex items-center justify-center size-14 rounded-full bg-accent-50 text-accent-700">
          <Sparkles size={28} />
        </div>
        <h1 className="text-3xl font-bold text-slate-900">
          Welcome to Mission Control
        </h1>
        <p className="text-base text-slate-600">
          This workspace hasn't been initialised yet. Run{" "}
          <code className="font-mono bg-slate-100 px-1.5 py-0.5 rounded text-slate-800">
            agenteval init
          </code>{" "}
          to start tracking eval runs, then refresh this page.
        </p>
      </header>

      <section className="rounded-lg border border-slate-200 bg-white p-5 space-y-4">
        <h2 className="text-sm font-semibold text-slate-700 flex items-center gap-2">
          <Terminal size={14} />
          Get started in three commands
        </h2>
        <ol className="space-y-3 text-sm">
          <Step
            n={1}
            title="Initialise the workspace"
            command="agenteval init"
            description="Creates .agenteval/ + solution.json in the current directory."
          />
          <Step
            n={2}
            title="Run an evaluation"
            command={`agenteval bench agentic --subject MyAgent`}
            description="Or any other bench / scenario you have. Results land in .agenteval/."
          />
          <Step
            n={3}
            title="Refresh this page"
            command={null}
            description="Mission Control will pick up the new workspace and surface the dashboard, runs list, evaluators, and compliance views."
          />
        </ol>
      </section>

      {root && (
        <section className="rounded-lg border border-slate-200 bg-slate-50 p-4 text-sm">
          <div className="flex items-start gap-2 text-slate-700">
            <FolderOpen size={14} className="shrink-0 mt-0.5 text-slate-500" />
            <div>
              <span className="text-slate-500">Currently watching: </span>
              <code className="font-mono text-xs break-all">{root}</code>
              <p className="text-xs text-slate-500 mt-1">
                Override with{" "}
                <code className="font-mono bg-white px-1 rounded">
                  agenteval mc serve --workspace &lt;path&gt;
                </code>{" "}
                to point at a different directory.
              </p>
            </div>
          </div>
        </section>
      )}

      <section className="rounded-lg border border-slate-200 bg-white p-5 space-y-2">
        <h2 className="text-sm font-semibold text-slate-700">Learn more</h2>
        <ul className="space-y-1.5 text-sm">
          <li>
            <a
              href="https://github.com/AgentEvalHQ/AgentEval#readme"
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 text-accent-700 hover:text-accent-500"
            >
              AgentEval README <ExternalLink size={12} />
            </a>
          </li>
          <li>
            <a
              href="/api/v1/version"
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 text-accent-700 hover:text-accent-500"
            >
              REST /api/v1/version <ExternalLink size={12} />
            </a>
          </li>
          <li>
            <a
              href="/graphql"
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 text-accent-700 hover:text-accent-500"
            >
              GraphQL playground (Nitro) <ExternalLink size={12} />
            </a>
          </li>
        </ul>
      </section>

      <p className="text-center text-xs text-slate-400">
        AgentEval {agentEvalVersion} · Mission Control
      </p>
    </div>
  );
}

function Step({
  n,
  title,
  command,
  description,
}: {
  n: number;
  title: string;
  command: string | null;
  description: string;
}) {
  return (
    <li className="flex items-start gap-3">
      <span className="shrink-0 size-6 rounded-full bg-accent-700 text-white text-xs font-semibold inline-flex items-center justify-center mt-0.5">
        {n}
      </span>
      <div className="flex-1">
        <h3 className="text-sm font-medium text-slate-900">{title}</h3>
        {command && (
          <pre className="mt-1.5 px-3 py-2 rounded bg-slate-900 text-slate-100 text-xs font-mono overflow-x-auto">
            {command}
          </pre>
        )}
        <p className="text-xs text-slate-600 mt-1">{description}</p>
      </div>
    </li>
  );
}
