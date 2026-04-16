
export default function WelcomePage() {
  return (
    <div className="flex flex-col items-center justify-center h-full p-4 sm:p-8">
      <div className="flex flex-col items-center gap-4 max-w-md text-center animate-[fade-in_0.4s_ease-out_both]">
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
          <p className="text-sm text-muted-foreground font-mono">
            Agent Fleet
          </p>
        </div>
        <p className="text-sm text-muted-foreground leading-relaxed mt-2">
          Welcome to Weave Agent Fleet. Select a view from the sidebar to get
          started, or create a new session.
        </p>
        <p className="mt-6 text-xs text-muted-foreground/50">
          v{import.meta.env.VITE_APP_VERSION} · {import.meta.env.VITE_COMMIT_SHA}
        </p>
      </div>
    </div>
  );
}
