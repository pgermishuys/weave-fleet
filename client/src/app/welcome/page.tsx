"use client";

import Image from "next/image";

export default function WelcomePage() {
  return (
    <div className="flex flex-col items-center justify-center h-full p-4 sm:p-8">
      <div className="flex flex-col items-center gap-4 max-w-md text-center">
        <Image
          src="/weave_logo.png"
          alt="Weave"
          width={72}
          height={72}
          className="rounded-xl"
        />
        <div>
          <h1 className="text-2xl font-bold font-mono weave-gradient-text">
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
        <p className="mt-6 text-[10px] text-muted-foreground/50">
          v{process.env.NEXT_PUBLIC_APP_VERSION} · {process.env.NEXT_PUBLIC_COMMIT_SHA}
        </p>
      </div>
    </div>
  );
}
