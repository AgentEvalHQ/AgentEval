import { Bot, Gavel } from "lucide-react";

// Plan-08 Wave 3: model badges. The agent's model and the judge's model are
// distinct (per plan-07 review F31 — both end up rendered on a run page).
// Different icon + label for instant disambiguation.

interface ModelBadgeProps {
  model: string | null | undefined;
  /** "agent" labels with Bot + neutral background; "judge" with Gavel + accent. */
  role: "agent" | "judge";
}

export function ModelBadge({ model, role }: ModelBadgeProps) {
  if (!model) return null;
  const Icon = role === "agent" ? Bot : Gavel;
  const tone =
    role === "agent"
      ? "bg-slate-100 text-slate-700"
      : "bg-accent-50 text-accent-700";
  const label = role === "agent" ? "Agent" : "Judge";
  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${tone}`}
      title={`${label} model`}
    >
      <Icon size={12} />
      <code className="font-mono">{model}</code>
    </span>
  );
}
