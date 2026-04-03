"use client";

import dynamic from "next/dynamic";

// Loaded dynamically so the server page.tsx has no direct client import,
// ensuring webpack's RSC analysis sees page.tsx as a pure server module.
const SessionDetailPageClient = dynamic(() => import("./_page-client"), { ssr: false });

export default SessionDetailPageClient;
