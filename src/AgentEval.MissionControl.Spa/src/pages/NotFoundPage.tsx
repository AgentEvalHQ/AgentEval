import { Link } from "react-router-dom";

export function NotFoundPage() {
  return (
    <div className="grid place-items-center min-h-[50vh]">
      <div className="text-center space-y-3">
        <h2 className="text-3xl font-bold text-slate-900">404</h2>
        <p className="text-sm text-slate-600">
          The route you requested doesn&apos;t exist yet.
        </p>
        <Link
          to="/"
          className="inline-block px-4 py-2 rounded-md bg-accent-700 text-white text-sm font-medium hover:bg-accent-500"
        >
          Back to dashboard
        </Link>
      </div>
    </div>
  );
}
