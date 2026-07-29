/**
 * Vite plugin to mock API responses for standalone frontend development.
 * 
 * Intercepts requests to `/api/*` and `/healthz` and returns canned JSON fixtures.
 * Only activates in dev/preview mode when MOCK_API env var is set or FLEET_API_URL is not set.
 * 
 * Usage:
 *   - Set MOCK_API=true to force mock mode
 *   - Set FLEET_API_URL to bypass mocking and use real backend
 */

import type { Plugin, ViteDevServer } from "vite";
import { readFileSync } from "fs";
import { resolve } from "path";

interface MockRoute {
  pattern: RegExp;
  handler: (url: URL, req: Request) => Response | Promise<Response>;
}

export function mockApiPlugin(): Plugin {
  const mockDir = resolve(__dirname, "src/mocks");
  
  // Load fixtures at plugin initialization
  const fixtures = {
    sessions: JSON.parse(readFileSync(resolve(mockDir, "sessions.json"), "utf-8")),
    fleetSummary: JSON.parse(readFileSync(resolve(mockDir, "fleet-summary.json"), "utf-8")),
    config: JSON.parse(readFileSync(resolve(mockDir, "config.json"), "utf-8")),
    projects: JSON.parse(readFileSync(resolve(mockDir, "projects.json"), "utf-8")),
  };

  const routes: MockRoute[] = [
    {
      pattern: /^\/api\/sessions(\?.*)?$/,
      handler: () => {
        console.log("[mock-api] GET /api/sessions");
        return new Response(JSON.stringify(fixtures.sessions), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/fleet\/summary$/,
      handler: () => {
        console.log("[mock-api] GET /api/fleet/summary");
        return new Response(JSON.stringify(fixtures.fleetSummary), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/config$/,
      handler: () => {
        console.log("[mock-api] GET /api/config");
        return new Response(JSON.stringify(fixtures.config), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/projects(\?.*)?$/,
      handler: () => {
        console.log("[mock-api] GET /api/projects");
        return new Response(JSON.stringify(fixtures.projects), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/healthz$/,
      handler: () => {
        console.log("[mock-api] GET /healthz");
        return new Response(JSON.stringify({ status: "ok" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/harnesses$/,
      handler: () => {
        console.log("[mock-api] GET /api/harnesses");
        return new Response(JSON.stringify([
          {
            type: "opencode",
            displayName: "OpenCode",
            available: true,
            userEnabled: true,
            capabilities: {
              requiresInitialPrompt: false,
              supportsAgents: true,
              supportsModelSelection: true,
              supportsCommands: true,
              supportsForking: true,
              supportsResume: true,
              supportsImageAttachments: true,
              supportsStreaming: true,
              supportsDelegation: true,
            },
          },
        ]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/workspace-roots$/,
      handler: () => {
        console.log("[mock-api] GET /api/workspace-roots");
        return new Response(JSON.stringify({
          roots: [
            {
              id: "mock-root-1",
              path: "C:\\Users\\demo\\projects",
              source: "user",
              exists: true,
            },
          ],
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/preferences$/,
      handler: () => {
        console.log("[mock-api] GET /api/preferences");
        return new Response(JSON.stringify({}), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/available-tools$/,
      handler: () => {
        console.log("[mock-api] GET /api/available-tools");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/plugins$/,
      handler: () => {
        console.log("[mock-api] GET /api/plugins");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/repositories$/,
      handler: () => {
        console.log("[mock-api] GET /api/repositories");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/.*$/,
      handler: (url) => {
        console.warn(`[mock-api] Unhandled API route: ${url.pathname}`);
        return new Response(JSON.stringify({ error: "Not implemented in mock" }), {
          status: 501,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
  ];

  function shouldMock(): boolean {
    // Force mock mode if MOCK_API=true
    if (process.env.MOCK_API === "true") {
      return true;
    }
    
    // Default: disabled until mock data shapes match the real API contract
    return false;
  }

  return {
    name: "vite-plugin-mock-api",
    
    config(config, { command }) {
      if (!shouldMock()) {
        console.log("[mock-api] Disabled (FLEET_API_URL is set or MOCK_API=false)");
        return;
      }

      if (command === "serve") {
        // Clear VITE_API_BASE_URL so the browser makes relative requests
        // that flow through Vite's middleware (where we intercept them).
        // Without this, .env.development.local sends requests directly
        // to localhost:5001, bypassing the mock entirely.
        console.log("[mock-api] Clearing VITE_API_BASE_URL and proxy (mock mode active)");
        return {
          define: {
            "import.meta.env.VITE_API_BASE_URL": JSON.stringify(""),
          },
          server: {
            ...config.server,
            proxy: undefined,
          },
        };
      }
    },
    
    configureServer(server: ViteDevServer) {
      if (!shouldMock()) {
        return;
      }

      console.log("[mock-api] Enabled - intercepting /api/* and /healthz requests");
      
      server.middlewares.use((req, res, next) => {
        const url = new URL(req.url || "/", `http://${req.headers.host}`);
        
        // Find matching route
        for (const route of routes) {
          if (route.pattern.test(url.pathname)) {
            const mockRequest = new Request(url.toString(), {
              method: req.method,
              headers: req.headers as HeadersInit,
            });
            
            Promise.resolve(route.handler(url, mockRequest))
              .then((response) => {
                res.statusCode = response.status;
                response.headers.forEach((value, key) => {
                  res.setHeader(key, value);
                });
                return response.text();
              })
              .then((body) => {
                res.end(body);
              })
              .catch((error) => {
                console.error("[mock-api] Handler error:", error);
                res.statusCode = 500;
                res.setHeader("Content-Type", "application/json");
                res.end(JSON.stringify({ error: "Mock handler error" }));
              });
            
            return; // Don't call next()
          }
        }
        
        next();
      });
    },
    
    configurePreviewServer(server: ViteDevServer) {
      // Reuse the same middleware for preview mode
      if (!shouldMock()) {
        return;
      }

      console.log("[mock-api] Enabled in preview mode - intercepting /api/* and /healthz requests");
      
      server.middlewares.use((req, res, next) => {
        const url = new URL(req.url || "/", `http://${req.headers.host}`);
        
        // Find matching route
        for (const route of routes) {
          if (route.pattern.test(url.pathname)) {
            const mockRequest = new Request(url.toString(), {
              method: req.method,
              headers: req.headers as HeadersInit,
            });
            
            Promise.resolve(route.handler(url, mockRequest))
              .then((response) => {
                res.statusCode = response.status;
                response.headers.forEach((value, key) => {
                  res.setHeader(key, value);
                });
                return response.text();
              })
              .then((body) => {
                res.end(body);
              })
              .catch((error) => {
                console.error("[mock-api] Handler error:", error);
                res.statusCode = 500;
                res.setHeader("Content-Type", "application/json");
                res.end(JSON.stringify({ error: "Mock handler error" }));
              });
            
            return; // Don't call next()
          }
        }
        
        next();
      });
    },
  };
}
