import { Routes, Route } from "react-router";
import { ClientLayout } from "./app/client-layout";
import { lazy, Suspense } from "react";
import { Loader2 } from "lucide-react";

// Lazy-load all pages for code splitting (same behavior as Next.js)
const FleetPage = lazy(() => import("./app/page"));
const AnalyticsPage = lazy(() => import("./app/analytics/page"));
const GitHubPage = lazy(() => import("./app/github/page"));
const GitHubRepoPage = lazy(() => import("./app/github/[owner]/[repo]/_page-client"));
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

export function App() {
  return (
    <ClientLayout>
      <Suspense fallback={<PageFallback />}>
        <Routes>
          <Route path="/" element={<FleetPage />} />
          <Route path="/analytics" element={<AnalyticsPage />} />
          <Route path="/github" element={<GitHubPage />} />
          <Route path="/github/:owner/:repo" element={<GitHubRepoPage />} />
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
      </Suspense>
    </ClientLayout>
  );
}
