import { useMemo } from "react";
import { useSearchParams } from "react-router";
import { apiUrl } from "@/lib/api-client";

export default function LoginPage() {
  const [searchParams] = useSearchParams();

  const returnUrl = useMemo(() => {
    const param = searchParams.get("returnUrl");
    return param ?? "/";
  }, [searchParams]);

  const signInHref = apiUrl(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);

  return (
    <div className="flex min-h-screen flex-col items-center justify-center bg-background p-4">
      <div className="flex flex-col items-center gap-6 max-w-sm w-full text-center">
        <img
          src="/weave_logo.png"
          alt="Weave"
          width={72}
          height={72}
          className="rounded-xl"
        />
        <div>
          <h1 className="text-3xl font-bold font-mono weave-gradient-text">
            Weave
          </h1>
          <p className="text-sm text-muted-foreground font-mono mt-1">
            Agent Fleet
          </p>
        </div>
        <p className="text-sm text-muted-foreground leading-relaxed">
          Orchestrate AI coding agents across your projects
        </p>
        <div className="flex flex-col gap-3 w-full">
          <a
            href={signInHref}
            className="inline-flex items-center justify-center rounded-md px-6 py-2.5 text-sm font-medium text-white weave-gradient-bg hover:opacity-90 transition-opacity"
          >
            Sign in
          </a>
          <a
            href={signInHref}
            className="inline-flex items-center justify-center rounded-md px-6 py-2.5 text-sm font-medium border border-border bg-transparent text-foreground hover:bg-muted transition-colors"
          >
            Sign up
          </a>
        </div>
        <p className="mt-4 text-[10px] text-muted-foreground/50">
          v{import.meta.env.VITE_APP_VERSION} · {import.meta.env.VITE_COMMIT_SHA}
        </p>
      </div>
    </div>
  );
}
