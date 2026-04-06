
import { Header } from "@/components/layout/header";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { AnalyticsDateFilter } from "@/components/analytics/analytics-date-filter";
import { OverviewTab } from "@/components/analytics/overview-tab";
import { ProjectsTab } from "@/components/analytics/projects-tab";
import { SessionsTab } from "@/components/analytics/sessions-tab";
import { ModelsTab } from "@/components/analytics/models-tab";
import { useAnalyticsFilters } from "@/hooks/use-analytics-filters";
import { useAnalyticsSummary } from "@/hooks/use-analytics-summary";
import { useAnalyticsDaily } from "@/hooks/use-analytics-daily";
import { useAnalyticsSessions } from "@/hooks/use-analytics-sessions";
import { useAnalyticsModels } from "@/hooks/use-analytics-models";

export default function AnalyticsPage() {
  const { filters, setFrom, setTo, setProjectId, resetFilters } = useAnalyticsFilters();

  const { summary, isLoading: summaryLoading } = useAnalyticsSummary(filters);
  const { daily, isLoading: dailyLoading } = useAnalyticsDaily(filters);
  const { sessions, isLoading: sessionsLoading } = useAnalyticsSessions(filters);
  const { models, isLoading: modelsLoading } = useAnalyticsModels(filters);

  // Derive project list for filter dropdown from summary.topProjects
  const projects = summary?.topProjects?.map((p) => ({ id: p.name, name: p.name })) ?? [];

  return (
    <div className="flex flex-col h-full">
      <Header
        title="Analytics"
        subtitle="Token usage, costs, and session statistics"
      />
      <div className="flex-1 overflow-auto p-3 sm:p-4 lg:p-6 space-y-4 sm:space-y-6">
        <AnalyticsDateFilter
          from={filters.from}
          to={filters.to}
          projectId={filters.projectId}
          projects={projects}
          onFromChange={setFrom}
          onToChange={setTo}
          onProjectChange={setProjectId}
          onReset={resetFilters}
        />
        <Tabs defaultValue="overview">
          <TabsList variant="line">
            <TabsTrigger value="overview">Overview</TabsTrigger>
            <TabsTrigger value="projects">Projects</TabsTrigger>
            <TabsTrigger value="sessions">Sessions</TabsTrigger>
            <TabsTrigger value="models">Models</TabsTrigger>
          </TabsList>
          <TabsContent value="overview" className="mt-4">
            <OverviewTab
              summary={summary}
              daily={daily}
              isLoading={summaryLoading || dailyLoading}
            />
          </TabsContent>
          <TabsContent value="projects" className="mt-4">
            <ProjectsTab
              projects={summary?.topProjects ?? []}
              isLoading={summaryLoading}
            />
          </TabsContent>
          <TabsContent value="sessions" className="mt-4">
            <SessionsTab sessions={sessions} isLoading={sessionsLoading} />
          </TabsContent>
          <TabsContent value="models" className="mt-4">
            <ModelsTab models={models} isLoading={modelsLoading} />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
