import type { Metadata } from "next";
import "./globals.css";
import { ClientLayout } from "./client-layout";

// Static export: title is baked at build time using the NEXT_PUBLIC_WEAVE_PROFILE env var.
// At runtime, the /api/profile endpoint provides the authoritative value — used by the
// profile badge in the sidebar. This layout only provides the initial <title> tag.
function getTitle(): string {
  const profile = process.env.NEXT_PUBLIC_WEAVE_PROFILE;
  if (!profile || profile === "default") return "Weave Agent Fleet";
  return `Weave Agent Fleet [${profile}]`;
}

export function generateMetadata(): Metadata {
  return {
    title: getTitle(),
    description: "Multi-agent orchestration dashboard for Weave — spawn, manage, and coordinate agent sessions across projects.",
    appleWebApp: {
      capable: true,
      statusBarStyle: "black-translucent",
      title: "Weave",
    },
    icons: {
      icon: [
        {
          url: "data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><defs><linearGradient id='g' x1='0%25' y1='0%25' x2='100%25' y2='100%25'><stop offset='0%25' stop-color='%233B82F6'/><stop offset='50%25' stop-color='%23A855F7'/><stop offset='100%25' stop-color='%23EC4899'/></linearGradient></defs><text x='50' y='75' font-size='80' font-weight='bold' font-family='system-ui' text-anchor='middle' fill='url(%23g)'>W</text></svg>",
          type: "image/svg+xml",
        },
      ],
      apple: "/weave_logo.png",
    },
  };
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className="dark" suppressHydrationWarning>
      <head>
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
        <meta name="theme-color" content="#0F172A" />
        <script dangerouslySetInnerHTML={{ __html: `(function(){try{var t=JSON.parse(localStorage.getItem('weave-theme'));var h=document.documentElement;if(t==='black'){h.classList.add('dark','theme-black');}else if(t==='light'){h.classList.remove('dark');h.classList.add('theme-light');}else{h.classList.add('dark');}}catch(e){}})();` }} />
      </head>
      <body className="antialiased">
        <ClientLayout>{children}</ClientLayout>
      </body>
    </html>
  );
}

