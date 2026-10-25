import { ShieldCheck, ShieldAlert } from "lucide-react";

// Container verification and recorded-hash comparison are separate claims.
// Neither establishes original execution custody or signature trust.

interface ManifestHashBadgeProps {
  contentHash: string | null;
  /** When provided, the badge compares to it and renders red on mismatch. */
  expectedHash?: string;
  integrity?: string;
  imported?: boolean;
}

export function ManifestHashBadge({ contentHash, expectedHash, integrity, imported }: ManifestHashBadgeProps) {
  if (integrity) {
    return <span className="inline-flex items-center gap-1 text-xs text-slate-600"
      title="Container integrity is separate from original execution custody and signature trust.">
      {integrity === "intact" ? <ShieldCheck size={12} /> : <ShieldAlert size={12} />}
      {integrity === "intact" ? "AEF intact" : integrity}{imported ? " · imported; not sealed when it ran" : ""}
    </span>;
  }
  if (!contentHash) {
    return (
      <span className="inline-flex items-center gap-1 text-xs text-slate-500">
        <ShieldAlert size={12} /> No content hash
      </span>
    );
  }

  const verified = expectedHash !== undefined && contentHash === expectedHash;
  const mismatch = expectedHash !== undefined && !verified;

  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${
        verified
          ? "bg-green-50 text-green-700"
          : mismatch ? "bg-red-50 text-red-700" : "bg-slate-50 text-slate-600"
      }`}
      title={verified ? "Recorded hashes agree; original execution integrity is not verified." : mismatch ? "Recorded hashes differ." : "Recorded hash; integrity not verified."}
    >
      {verified ? <ShieldCheck size={12} /> : <ShieldAlert size={12} />}
      <code className="font-mono">{contentHash.slice(0, 8)}…</code>
    </span>
  );
}
