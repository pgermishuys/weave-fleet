import { Routes, Route } from "react-router";
import { ClientLayout } from "./app/client-layout";
import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { Loader2 } from "lucide-react";
import { usePluginRuntime } from "@/plugins/context";
import { getRoutes } from "@/plugins/slots";
import { apiFetch, apiUrl } from "@/lib/api-client";
import type { ClientConfigResponse, UserMeResponse } from "@/lib/api-types";
import { AppShellProvider } from "@/contexts/app-shell-context";

// Lazy-load all pages for code splitting (same behavior as Next.js)
const FleetPage = lazy(() => import("./app/page"));
const AnalyticsPage = lazy(() => import("./app/analytics/page"));
const IntegrationsPage = lazy(() => import("./app/integrations/page"));
const PipelinesPage = lazy(() => import("./app/pipelines/page"));
const QueuePage = lazy(() => import("./app/queue/page"));
const RepositoriesPage = lazy(() => import("./app/repositories/page"));
const RepositoryDetailPage = lazy(() => import("./app/repositories/[path]/_page-client"));
const SessionDetailPage = lazy(() => import("./app/sessions/[id]/_page-client"));
const SettingsPage = lazy(() => import("./app/settings/page"));
const TemplatesPage = lazy(() => import("./app/templates/page"));
const WelcomePage = lazy(() => import("./app/welcome/page"));
const NotFoundPage = lazy(() => import("./app/not-found"));

function PageFallback() {
  return (
    <div className="flex items-center justify-center h-full text-muted-foreground gap-2 text-sm">
      <Loader2 className="h-4 w-4 animate-spin" />
      Loading...
    </div>
  );
}

function AuthGate({ children }: { children: React.ReactNode }) {
  const [status, setStatus] = useState<"checking" | "ready" | "error">("checking");
  const [clientConfig, setClientConfig] = useState<ClientConfigResponse | null>(null);
  const [currentUser, setCurrentUser] = useState<UserMeResponse | null>(null);
  const loginUrl = useMemo(() => {
    if (typeof window === "undefined") {
      return "/auth/login";
    }

    const returnUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
    return apiUrl(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }, []);

  useEffect(() => {
    let active = true;

    void (async () => {
      try {
        const configResponse = await apiFetch("/api/config/client");

        if (configResponse.status === 401) {
          window.location.assign(loginUrl);
          return;
        }

        if (!configResponse.ok) {
          if (active) {
            setStatus("error");
          }

          return;
        }

        const config = (await configResponse.json()) as ClientConfigResponse;
        const response = await apiFetch("/api/user/me");

        if (response.ok) {
          if (active) {
            setClientConfig(config);
            setCurrentUser((await response.json()) as UserMeResponse);
            setStatus("ready");
          }

          return;
        }

        if (response.status === 401 && config.authEnabled) {
          window.location.assign(loginUrl);
          return;
        }

        if (active) {
          setStatus("error");
        }
      } catch {
        if (active) {
          setStatus("error");
        }
      }
    })();

    return () => {
      active = false;
    };
  }, [loginUrl]);

  if (status === "checking") {
    return <PageFallback />;
  }

  if (status === "error") {
    return (
      <div className="flex h-screen items-center justify-center text-sm text-muted-foreground">
        Unable to verify sign-in status.
      </div>
    );
  }

  if (!clientConfig) {
    return null;
  }

  return (
    <AppShellProvider clientConfig={clientConfig} currentUser={currentUser}>
      {children}
    </AppShellProvider>
  );
}

function AppRoutes() {
  const { manifests } = usePluginRuntime();
  const pluginRoutes = getRoutes(manifests);

  return (
    <Routes>
      <Route path="/" element={<FleetPage />} />
      <Route path="/analytics" element={<AnalyticsPage />} />
      {pluginRoutes.map((route) => (
        <Route
          key={`${route.pluginId}:${route.path ?? route.id ?? "route"}`}
          path={typeof route.path === "string" ? route.path : undefined}
          element={route.element}
        />
      ))}
      <Route path="/integrations" element={<IntegrationsPage />} />
      <Route path="/pipelines" element={<PipelinesPage />} />
      <Route path="/queue" element={<QueuePage />} />
      <Route path="/repositories" element={<RepositoriesPage />} />
      <Route path="/repositories/:path" element={<RepositoryDetailPage />} />
      <Route path="/sessions/:id" element={<SessionDetailPage />} />
      <Route path="/settings" element={<SettingsPage />} />
      <Route path="/templates" element={<TemplatesPage />} />
      <Route path="/welcome" element={<WelcomePage />} />
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}

export function App() {
  return (
    <AuthGate>
      <ClientLayout>
        <Suspense fallback={<PageFallback />}>
          <AppRoutes />
        </Suspense>
      </ClientLayout>
    </AuthGate>
  );
}
