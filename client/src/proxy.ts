import { NextRequest, NextResponse } from "next/server";

/**
 * CORS proxy for API routes.
 *
 * Adds permissive CORS headers to all `/api/` responses so the frontend
 * can be served from a different origin (e.g. Tauri webview or a separate
 * dev server on port 3001).
 *
 * In standalone mode (single-origin), these headers are harmless no-ops.
 */

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, PUT, PATCH, DELETE, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type, Authorization",
};

export function proxy(request: NextRequest) {
  // Preflight requests — respond immediately with 204
  if (request.method === "OPTIONS") {
    return new NextResponse(null, {
      status: 204,
      headers: CORS_HEADERS,
    });
  }

  // All other requests — add CORS headers and continue
  const response = NextResponse.next();
  for (const [key, value] of Object.entries(CORS_HEADERS)) {
    response.headers.set(key, value);
  }
  return response;
}

export const config = {
  matcher: "/api/:path*",
};
