# Analytics Frontend Dashboard — Implementation Plan

## TL;DR
> **Summary**: Build a `/analytics` dashboard page with four tabs (Overview, Projects, Sessions, Models) powered by Recharts, with date range filtering, data fetching hooks, and sidebar navigation integration — consuming the API endpoints defined in the analytics backend plan.
> **Estimated Effort**: Medium

## Context

### Original Request
Build a frontend analytics dashboard where users can view statistics of token usage by project and by session. This extends the analytics backend plan (`.weave/plans/analytics-store.md`) which provides API endpoints at `/api/analytics/*`.

### Key Findings

**Frontend stack**: Next.js 16 + React 19 + TypeScript 5.9, shadcn/ui (Radix primitives) + Tailwind CSS 4 + Lucide icons. All pages live under `client/src/app/`. UI primitives are in `client/src/components/ui/` (Card, Tabs, Badge, Select, Popover, Input, Button, etc.).

**Data fetching pattern**: Custom `apiFetch(path, init?)` wrapper in `client/src/lib/api-client.ts` that prepends a configurable API base URL. Hooks follow a consistent polling pattern (see `use-sessions.ts`, `use-fleet-summary.ts`):
- `useState` for data, loading, error
- `useCallback` for the fetch function
- `useEffect` with `setInterval(fetch, pollIntervalMs)` + `visibilitychange` listener
- `useRef(true)` for mounted guard
- Default poll intervals: 15s for sessions, 30s for fleet summary

**Page structure pattern**: Pages use `"use client"` directive, render a `<Header>` component with title/subtitle/actions, then a scrollable content area with `p-3 sm:p-4 lg:p-6` padding. The fleet page (`client/src/app/page.tsx`) uses `<SummaryBar>` for stat cards — a responsive `grid grid-cols-2 sm:grid-cols-4` of bordered cards with icon + value + label.

**Tabbed page pattern**: Settings page (`client/src/app/settings/page.tsx`) uses `<Tabs defaultValue="skills">` with `<TabsList variant="line">` for a horizontal line-style tab bar. Each tab renders a separate component via `<TabsContent value="..." className="mt-4">`. This is the exact pattern to follow.

**Navigation pattern**: Sidebar icon rail (`client/src/components/layout/sidebar-icon-rail.tsx`) has two types of nav items:
1. **`IconRailButton`** — for views with sidebar panels (fleet, github, repositories). Uses `SidebarView` type from context.
2. **`IconRailLink`** — for standalone pages without sidebar panels (Settings). Uses `<Link href="...">` with pathname-based active state.
The analytics page is a standalone page (no sidebar panel needed), so it should use `IconRailLink` — same pattern as the Settings link.

**`SidebarView` type**: Defined in `client/src/contexts/sidebar-context.tsx` as `"welcome" | "fleet" | "github" | "repositories"`. This type does NOT need to change for analytics — `IconRailLink` doesn't use `SidebarView`, it just checks `pathname.startsWith(href)` for active state.

**`viewForPathname` function**: Maps pathnames to views. Returns `null` for unrecognized paths (like `/settings`). The analytics route (`/analytics`) should also return `null` — it's a standalone page, not a sidebar view.

**No charting library**: `package.json` has no charting dependency. Need to add one.

**Existing format utilities**: `client/src/lib/format-utils.ts` has `formatTokens(n)` (→ "1.5k"), `formatCost(n)` (→ "$1.50"), `formatDuration(s)` (→ "5m 30s"), `formatTimestamp(ms)`, `formatRelativeTime(ts, now?)`.

**Types location**: Domain types in `client/src/lib/types.ts`, API response types in `client/src/lib/api-types.ts`.

**Test pattern**: Vitest with jsdom environment, `describe`/`it` blocks, tests in `__tests__/` subdirectories. Tests for pure utility functions and hooks.

**Backend API endpoints** (from analytics-store.md):
- `GET /api/analytics/summary?from=&to=&projectId=` → `AnalyticsSummary`
- `GET /api/analytics/daily?from=&to=&projectId=` → `DailyAnalytics[]`
- `GET /api/analytics/sessions?from=&to=&projectId=&limit=50` → `SessionAnalytics[]`
- `GET /api/analytics/models?from=&to=` → `ModelAnalytics[]`
- `GET /api/analytics/export?from=&to=&projectId=&format=json` → `TokenEventRow[]`

**Backend DTO shapes** (from analytics-store.md):
- `AnalyticsSummary`: TotalTokens, TotalCost, TotalEstimatedCost, SessionCount, MessageCount, TopModels[], TopProjects[]
- `DailyAnalytics`: Date, Tokens, Cost, EstimatedCost, Sessions, Messages
- `SessionAnalytics`: SessionId, Title, ProjectId, ProjectName, Tokens, Cost, EstimatedCost, Models[], DurationSeconds, CreatedAt
- `ModelAnalytics`: ModelId, ProviderId, Tokens, Cost, EstimatedCost, MessageCount, AvgCostPerMessage

## Objectives

### Core Objective
Provide a clear, actionable analytics dashboard for token usage and cost tracking across projects, sessions, and models — enabling users to understand their AI spend at a glance and drill into details.

### Deliverables
- [ ] Recharts charting library added to `client/package.json`
- [ ] TypeScript types for analytics API responses in `client/src/lib/api-types.ts`
- [ ] Data fetching hooks for all analytics endpoints
- [ ] Analytics page at `/analytics` with 4 tabs (Overview, Projects, Sessions, Models)
- [ ] Date range filter component shared across all tabs
- [ ] Sidebar icon rail integration (BarChart3 icon link)
- [ ] Dark mode support (automatic via CSS variables + Recharts theme)
- [ ] Responsive design (mobile-friendly)
- [ ] Unit tests for analytics utility functions and type contracts

### Definition of Done
- [ ] `npm run typecheck` passes in `client/`
- [ ] `npm run lint` passes in `client/`
- [ ] `npm test` passes all existing + new tests in `client/`
- [ ] Navigating to `/analytics` shows the dashboard with all 4 tabs
- [ ] BarChart3 icon appears in sidebar rail, highlights when on `/analytics`
- [ ] Date range filter controls data displayed across all tabs
- [ ] Charts render correctly in both dark and light themes
- [ ] Page is usable on mobile (single-column layout, horizontal scroll for tables)

### Guardrails (Must NOT)
- Never modify the backend API or C# code — this plan is frontend-only
- Never add a heavy charting library (D3 directly, Chart.js) — use Recharts for its React-native API
- Never add a date picker library (react-datepicker, etc.) — use native `<input type="date">` for simplicity
- Never add React Query, SWR, or other data fetching libraries — use the existing polling hook pattern
- Never add client-side state management beyond what's needed (no Redux, Zustand, etc.)
- Never break existing pages or navigation

## TODOs

### Phase 1: Dependencies & Types

- [ ] 1. **Install Recharts**
  **What**: Add `recharts` as a dependency. Recharts is ~45kB gzipped, React-native, works well with Tailwind CSS variables for dark mode theming. It's the de facto standard for React dashboards.
  **Files**:
  - Modify `client/package.json` — add `"recharts": "^2.15.3"` to dependencies
  **Commands**: `npm install recharts` in `client/`
  **Acceptance**: `import { LineChart, BarChart } from "recharts"` compiles without errors. `npm run typecheck` passes.

- [ ] 2. **Add Analytics API Types**
  **What**: Define TypeScript interfaces matching the backend analytics DTOs. Follow the camelCase convention used throughout `api-types.ts` (the backend serializes as camelCase in JSON).
  **Files**:
  - Modify `client/src/lib/api-types.ts` — add new section at the bottom
  **Types to add**:
  ```typescript
  // ─── Analytics Types ──────────────────────────────────────────────────────

  export interface AnalyticsSummary {
    totalTokens: number;
    totalCost: number;
    totalEstimatedCost: number;
    sessionCount: number;
    messageCount: number;
    topModels: AnalyticsTopItem[];
    topProjects: AnalyticsTopItem[];
  }

  export interface AnalyticsTopItem {
    name: string;
    tokens: number;
    cost: number;
  }

  export interface DailyAnalytics {
    date: string;          // ISO date string "YYYY-MM-DD"
    tokens: number;
    cost: number;
    estimatedCost: number;
    sessions: number;
    messages: number;
  }

  export interface SessionAnalytics {
    sessionId: string;
    title: string | null;
    projectId: string | null;
    projectName: string | null;
    tokens: number;
    cost: number;
    estimatedCost: number;
    models: string[];
    durationSeconds: number | null;
    createdAt: string;     // ISO datetime
  }

  export interface ModelAnalytics {
    modelId: string;
    providerId: string;
    tokens: number;
    cost: number;
    estimatedCost: number;
    messageCount: number;
    avgCostPerMessage: number;
  }
  ```
  **Acceptance**: Types compile. Existing types unaffected.

### Phase 2: Data Fetching Hooks

- [ ] 3. **Create `useAnalyticsSummary` Hook**
  **What**: Polling hook for `GET /api/analytics/summary`. Follows the exact pattern of `use-fleet-summary.ts` — `useState` for data/loading/error, `useCallback` fetch with `apiFetch`, `useEffect` with `setInterval` + `visibilitychange`. Accepts `from`, `to`, `projectId` as parameters. Poll interval: 30 seconds.
  **Files**:
  - Create `client/src/hooks/use-analytics-summary.ts`
  **Interface**:
  ```typescript
  interface UseAnalyticsSummaryParams {
    from?: string;    // ISO date string
    to?: string;      // ISO date string
    projectId?: string;
  }

  interface UseAnalyticsSummaryResult {
    summary: AnalyticsSummary | null;
    isLoading: boolean;
    error?: string;
  }

  function useAnalyticsSummary(
    params?: UseAnalyticsSummaryParams,
    pollIntervalMs?: number
  ): UseAnalyticsSummaryResult;
  ```
  **Query string construction**: Build URL params from non-null filter values. E.g. `/api/analytics/summary?from=2025-01-01&to=2025-01-31&projectId=abc`.
  **Acceptance**: Hook compiles. Returns typed data from the API endpoint.

- [ ] 4. **Create `useAnalyticsDaily` Hook**
  **What**: Polling hook for `GET /api/analytics/daily`. Same pattern. Returns `DailyAnalytics[]`.
  **Files**:
  - Create `client/src/hooks/use-analytics-daily.ts`
  **Interface**:
  ```typescript
  interface UseAnalyticsDailyResult {
    daily: DailyAnalytics[];
    isLoading: boolean;
    error?: string;
  }

  function useAnalyticsDaily(
    params?: { from?: string; to?: string; projectId?: string },
    pollIntervalMs?: number
  ): UseAnalyticsDailyResult;
  ```
  **Acceptance**: Hook compiles. Returns typed data.

- [ ] 5. **Create `useAnalyticsSessions` Hook**
  **What**: Polling hook for `GET /api/analytics/sessions`. Same pattern. Supports `limit` parameter (default 50). Returns `SessionAnalytics[]`.
  **Files**:
  - Create `client/src/hooks/use-analytics-sessions.ts`
  **Interface**:
  ```typescript
  interface UseAnalyticsSessionsResult {
    sessions: SessionAnalytics[];
    isLoading: boolean;
    error?: string;
  }

  function useAnalyticsSessions(
    params?: { from?: string; to?: string; projectId?: string; limit?: number },
    pollIntervalMs?: number
  ): UseAnalyticsSessionsResult;
  ```
  **Acceptance**: Hook compiles. Returns typed data.

- [ ] 6. **Create `useAnalyticsModels` Hook**
  **What**: Polling hook for `GET /api/analytics/models`. Same pattern. Returns `ModelAnalytics[]`.
  **Files**:
  - Create `client/src/hooks/use-analytics-models.ts`
  **Interface**:
  ```typescript
  interface UseAnalyticsModelsResult {
    models: ModelAnalytics[];
    isLoading: boolean;
    error?: string;
  }

  function useAnalyticsModels(
    params?: { from?: string; to?: string },
    pollIntervalMs?: number
  ): UseAnalyticsModelsResult;
  ```
  **Acceptance**: Hook compiles. Returns typed data.

- [ ] 7. **Create `useAnalyticsFilters` Hook**
  **What**: A simple state hook that manages the shared date range + project filter state across all analytics tabs. Uses `usePersistedState` (from `client/src/hooks/use-persisted-state.ts`) to persist filter selections in localStorage across page reloads.
  **Files**:
  - Create `client/src/hooks/use-analytics-filters.ts`
  **Interface**:
  ```typescript
  interface AnalyticsFilters {
    from: string;       // ISO date string, default: 30 days ago
    to: string;         // ISO date string, default: today
    projectId: string;  // "" means all projects
  }

  interface UseAnalyticsFiltersResult {
    filters: AnalyticsFilters;
    setFrom: (date: string) => void;
    setTo: (date: string) => void;
    setProjectId: (id: string) => void;
    resetFilters: () => void;
  }

  function useAnalyticsFilters(): UseAnalyticsFiltersResult;
  ```
  **LocalStorage key**: `"weave:analytics:filters"`
  **Default range**: Last 30 days (computed at hook initialization).
  **Acceptance**: Filters persist across page reloads. Changing filters re-fetches data.

### Phase 3: Shared Components

- [ ] 8. **Create `AnalyticsDateFilter` Component**
  **What**: A toolbar-style filter bar with two `<input type="date">` fields (From/To) and a project dropdown. Uses native date inputs for simplicity (no external date picker library). Styled to match the `FleetToolbar` aesthetic — compact, border-bottom separator, horizontal layout on desktop, stacked on mobile.
  **Files**:
  - Create `client/src/components/analytics/analytics-date-filter.tsx`
  **Props**:
  ```typescript
  interface AnalyticsDateFilterProps {
    from: string;
    to: string;
    projectId: string;
    projects: Array<{ id: string; name: string }>;  // from AnalyticsSummary.topProjects or a separate projects list
    onFromChange: (date: string) => void;
    onToChange: (date: string) => void;
    onProjectChange: (projectId: string) => void;
    onReset: () => void;
  }
  ```
  **Layout**: `flex flex-wrap items-center gap-2 sm:gap-3` with:
  - Label "From" + `<Input type="date" className="w-36 h-8" />`
  - Label "To" + `<Input type="date" className="w-36 h-8" />`
  - Project `<Select>` dropdown (using shadcn/ui Select) with "All Projects" as default
  - "Reset" button (ghost variant, small) to clear all filters
  **Styling**: Use the existing `Input` component from `client/src/components/ui/input.tsx` with `type="date"`. Use `Select`/`SelectTrigger`/`SelectContent`/`SelectItem` from `client/src/components/ui/select.tsx` for the project dropdown. Date inputs have native dark mode support via CSS color-scheme.
  **Acceptance**: Filter bar renders. Changing values calls the respective callbacks. "All Projects" option works.

- [ ] 9. **Create `StatCard` Component**
  **What**: A reusable stat card for the analytics overview — similar to the SummaryBar cards but slightly larger with optional secondary value. Uses the Card primitive.
  **Files**:
  - Create `client/src/components/analytics/stat-card.tsx`
  **Props**:
  ```typescript
  interface StatCardProps {
    icon: React.ComponentType<{ className?: string }>;
    iconColor: string;    // Tailwind text color class
    label: string;
    value: string;
    secondaryValue?: string;  // e.g. "estimated: $12.34"
  }
  ```
  **Layout**: Rounded border card (matching SummaryBar style) with centered icon, bold value, label below. Optional secondary line in muted text.
  **Acceptance**: Card renders with icon, value, label, and optional secondary value.

- [ ] 10. **Create Recharts Theme Utility**
  **What**: A utility that provides Recharts color tokens derived from CSS variables, ensuring charts look correct in dark/light/black themes. Recharts accepts hex/rgb colors, so we need to resolve CSS variable values at render time.
  **Files**:
  - Create `client/src/lib/chart-theme.ts`
  **Implementation**:
  ```typescript
  // Returns computed CSS variable values for Recharts theming
  export function getChartColors(): {
    primary: string;      // main chart line/bar color
    secondary: string;    // secondary series color
    accent: string;       // accent/highlight color
    muted: string;        // grid lines, axes
    background: string;   // tooltip background
    foreground: string;   // text/labels
  }

  // Palette of distinct colors for multi-series charts
  export const CHART_PALETTE = [
    "hsl(var(--chart-1, 220 70% 50%))",
    "hsl(var(--chart-2, 160 60% 45%))",
    "hsl(var(--chart-3, 30 80% 55%))",
    "hsl(var(--chart-4, 280 65% 60%))",
    "hsl(var(--chart-5, 340 75% 55%))",
  ];
  ```
  **Approach**: Use `getComputedStyle(document.documentElement).getPropertyValue("--foreground")` at call time. Cache briefly. Provide fallback values for SSR.
  **Note**: Recharts supports `hsl()` strings directly — we can use CSS custom property values without converting.
  **Acceptance**: Chart colors adapt to theme changes.

### Phase 4: Tab Components

- [ ] 11. **Create Overview Tab**
  **What**: The default tab showing summary stats + daily usage chart + top models + top projects. This is the "executive summary" view.
  **Files**:
  - Create `client/src/components/analytics/overview-tab.tsx`
  **Layout**:
  ```
  ┌─────────────┬──────────────┬──────────────┬──────────────┐
  │ Total Tokens │  Total Cost  │   Sessions   │   Messages   │
  │   1.2M       │   $45.67     │      42      │     1,284    │
  └─────────────┴──────────────┴──────────────┴──────────────┘
  ┌────────────────────────────────────────────────────────────┐
  │              Daily Usage Chart (Area/Line)                 │
  │  ──── Tokens    ──── Cost                                 │
  │                                                            │
  │  ┌───────────────────────────────────────────────────┐    │
  │  │ Area chart with X=date, Y=tokens/cost dual axis  │    │
  │  └───────────────────────────────────────────────────┘    │
  └────────────────────────────────────────────────────────────┘
  ┌─────────────────────────────┬──────────────────────────────┐
  │       Top Models            │        Top Projects          │
  │  claude-sonnet-4   $12.34  │  my-project       $8.50     │
  │  gpt-4o            $8.50   │  other-project    $5.20     │
  │  o4-mini           $3.20   │  ...                         │
  └─────────────────────────────┴──────────────────────────────┘
  ```
  **Components used**:
  - `StatCard` (4 cards in `grid grid-cols-2 sm:grid-cols-4` — same responsive grid as SummaryBar)
  - Recharts `AreaChart` or `LineChart` for daily usage (responsive container, dual Y axes for tokens + cost)
  - Simple ranked lists for top models/projects (no chart needed — just styled divs with horizontal bars showing relative proportion)
  **Icons**: `Hash` for tokens (purple), `DollarSign` for cost (green), `Monitor` for sessions (blue), `MessageSquare` for messages (orange) — all from Lucide.
  **Props**: `summary: AnalyticsSummary | null`, `daily: DailyAnalytics[]`, `isLoading: boolean`
  **Empty state**: Show placeholder text "No analytics data yet. Data will appear once sessions generate token usage." when summary is null or all zeros.
  **Acceptance**: Summary stats display correctly. Daily chart renders with proper axes. Top models/projects render as ranked lists.

- [ ] 12. **Create Projects Tab**
  **What**: Per-project breakdown showing tokens, cost, session count. Presented as a responsive card grid (matching the fleet page's card grid pattern).
  **Files**:
  - Create `client/src/components/analytics/projects-tab.tsx`
  **Layout**: Uses data from `AnalyticsSummary.topProjects` (summary endpoint already returns top projects with token/cost data). Each project is a card showing:
  - Project name (bold)
  - Total tokens (formatted)
  - Total cost (formatted)
  - A small horizontal bar chart showing cost proportion relative to the highest project
  **Grid**: `grid gap-3 sm:gap-4 sm:grid-cols-2 lg:grid-cols-3` (matches fleet page grid).
  **Props**: `projects: AnalyticsTopItem[]`, `isLoading: boolean`
  **Note**: For a more detailed breakdown, we could add a separate `/api/analytics/projects` endpoint in the future. For now, `topProjects` from the summary endpoint provides the key data. If we need more, we can fetch sessions filtered by projectId.
  **Acceptance**: Project cards render. Shows proportional cost bar. Empty state when no projects.

- [ ] 13. **Create Sessions Tab**
  **What**: Session-level analytics table with sortable columns. This is the most data-dense tab.
  **Files**:
  - Create `client/src/components/analytics/sessions-tab.tsx`
  **Layout**: A styled table (not a full data table library — just a `<table>` with Tailwind) with columns:
  - Title (truncated, left-aligned)
  - Project (badge, left-aligned)
  - Tokens (right-aligned, formatted)
  - Cost (right-aligned, formatted)
  - Duration (right-aligned, formatted)
  - Model(s) (badges)
  - Created (relative time, right-aligned)
  **Features**:
  - Client-side sorting on any column (click header to toggle asc/desc)
  - Uses `useAnalyticsSessions` hook data
  - Responsive: on mobile, hide Duration and Model columns (use `hidden sm:table-cell`)
  - Row hover highlight
  **Styling**: `text-sm`, `border-b border-border`, alternating row backgrounds via `even:bg-muted/30`. Headers use `text-xs font-semibold uppercase tracking-wider text-muted-foreground` (matching the fleet page group headers).
  **Sort state**: Local `useState<{ column: string; direction: "asc" | "desc" }>` — no need to persist.
  **Props**: `sessions: SessionAnalytics[]`, `isLoading: boolean`
  **Acceptance**: Table renders with all columns. Clicking headers sorts. Responsive on mobile.

- [ ] 14. **Create Models Tab**
  **What**: Per-model breakdown showing token usage, cost, message count, avg cost per message. Uses a combination of a horizontal bar chart and a detail list.
  **Files**:
  - Create `client/src/components/analytics/models-tab.tsx`
  **Layout**:
  ```
  ┌────────────────────────────────────────────────────────────┐
  │              Cost by Model (Horizontal Bar Chart)          │
  │  claude-sonnet-4  ████████████████████  $12.34             │
  │  gpt-4o           ████████████         $8.50              │
  │  o4-mini          ████                 $3.20              │
  └────────────────────────────────────────────────────────────┘
  ┌────────────────────────────────────────────────────────────┐
  │  Model Details Table                                       │
  │  Model      │ Provider │ Tokens  │ Cost  │ Messages │ Avg  │
  │  sonnet-4   │ anthro.  │ 125.4k  │$12.34 │ 342      │$0.04 │
  └────────────────────────────────────────────────────────────┘
  ```
  **Components used**:
  - Recharts `BarChart` (horizontal) for visual breakdown
  - Table for detailed metrics (same styling as sessions tab)
  **Props**: `models: ModelAnalytics[]`, `isLoading: boolean`
  **Acceptance**: Bar chart renders. Table shows all model metrics. Empty state when no data.

### Phase 5: Analytics Page & Navigation

- [ ] 15. **Create Analytics Page**
  **What**: The main `/analytics` route page component. Orchestrates filters, hooks, and tab components.
  **Files**:
  - Create `client/src/app/analytics/page.tsx`
  **Structure**: Follow the settings page pattern exactly:
  ```typescript
  "use client";

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
    const projects = summary?.topProjects?.map(p => ({ id: p.name, name: p.name })) ?? [];

    return (
      <div className="flex flex-col h-full">
        <Header title="Analytics" subtitle="Token usage, costs, and session statistics" />
        <div className="flex-1 overflow-auto p-3 sm:p-4 lg:p-6 space-y-4 sm:space-y-6">
          <AnalyticsDateFilter ... />
          <Tabs defaultValue="overview">
            <TabsList variant="line">
              <TabsTrigger value="overview">Overview</TabsTrigger>
              <TabsTrigger value="projects">Projects</TabsTrigger>
              <TabsTrigger value="sessions">Sessions</TabsTrigger>
              <TabsTrigger value="models">Models</TabsTrigger>
            </TabsList>
            <TabsContent value="overview" className="mt-4">
              <OverviewTab summary={summary} daily={daily} isLoading={summaryLoading || dailyLoading} />
            </TabsContent>
            <TabsContent value="projects" className="mt-4">
              <ProjectsTab projects={summary?.topProjects ?? []} isLoading={summaryLoading} />
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
  ```
  **Data fetching strategy**: All 4 hooks are called unconditionally at the page level. This pre-fetches data for all tabs. Since the poll interval is 30s and the data is small, this is efficient. Tabs render immediately when switched — no loading delay.
  **Acceptance**: Page renders at `/analytics`. All 4 tabs work. Filters apply across all tabs.

- [ ] 16. **Add Analytics Link to Sidebar Icon Rail**
  **What**: Add a `BarChart3` icon link to the sidebar icon rail's bottom section (alongside Settings), using the `IconRailLink` pattern.
  **Files**:
  - Modify `client/src/components/layout/sidebar-icon-rail.tsx`
  **Changes**:
  1. Add `BarChart3` to the Lucide import: `import { LayoutGrid, Github, Settings, FolderGit2, BarChart3 } from "lucide-react";`
  2. Add the analytics link in the bottom section, above the Settings link:
     ```tsx
     {/* Bottom section: page links + version */}
     <div className="flex flex-col gap-0.5 px-1">
       <IconRailLink icon={BarChart3} label="Analytics" href="/analytics" />
       <IconRailLink icon={Settings} label="Settings" href="/settings" />
       <ProfileBadge />
       <VersionBadge />
     </div>
     ```
  **Why bottom section**: Analytics is a standalone page link, same category as Settings. The top section is reserved for sidebar-panel views (Fleet, GitHub, Repositories). This keeps the information architecture clean.
  **Acceptance**: BarChart3 icon appears in sidebar. Clicking navigates to `/analytics`. Icon highlights when on `/analytics` route.

### Phase 6: Analytics Format Utilities

- [ ] 17. **Add Analytics-Specific Format Utilities**
  **What**: Add format helpers specific to analytics display (large numbers, percentage formatting, date range labels).
  **Files**:
  - Modify `client/src/lib/format-utils.ts` — add new functions
  **Functions to add**:
  ```typescript
  /** Format large numbers with M/K suffix: 1234567 → "1.2M", 45678 → "45.7K" */
  export function formatLargeNumber(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
    return n.toLocaleString();
  }

  /** Format cost with appropriate precision: $0.004 → "$0.004", $1.50 → "$1.50", $1234 → "$1,234" */
  export function formatAnalyticsCost(cost: number): string {
    if (cost === 0) return "$0.00";
    if (cost < 0.01) return `$${cost.toFixed(3)}`;
    if (cost >= 1000) return `$${cost.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    return `$${cost.toFixed(2)}`;
  }

  /** Format a date string as short display: "2025-01-15" → "Jan 15" */
  export function formatShortDate(dateStr: string): string {
    const d = new Date(dateStr + "T00:00:00");
    return d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
  }
  ```
  **Acceptance**: Utility functions work correctly. Existing functions unaffected.

### Phase 7: Tests

- [ ] 18. **Test Analytics Format Utilities**
  **What**: Unit tests for the new format functions.
  **Files**:
  - Modify `client/src/lib/__tests__/format-utils.test.ts` — add new describe blocks
  **Tests**:
  - `formatLargeNumber`: handles 0, hundreds, thousands (→ "K"), millions (→ "M"), boundary values
  - `formatAnalyticsCost`: handles 0, sub-cent (→ 3 decimal), normal (→ 2 decimal), thousands (→ comma-separated)
  - `formatShortDate`: handles ISO date strings, produces "Jan 15" format
  **Acceptance**: All new tests pass. Existing tests unaffected.

- [ ] 19. **Test Analytics Types Contract**
  **What**: Compile-time type assertions ensuring the analytics types match expected shapes. Similar to existing `api-types.test.ts`.
  **Files**:
  - Create `client/src/lib/__tests__/analytics-types.test.ts`
  **Tests**:
  - Verify `AnalyticsSummary` has all required fields with correct types
  - Verify `DailyAnalytics`, `SessionAnalytics`, `ModelAnalytics` shapes
  - Verify JSON fixture parsing (create a small fixture with sample API response, assert it parses to the correct types)
  **Acceptance**: Types are verified at compile time and test time.

- [ ] 20. **Test `useAnalyticsFilters` Hook**
  **What**: Unit tests for the filter state management hook.
  **Files**:
  - Create `client/src/hooks/__tests__/use-analytics-filters.test.ts`
  **Tests**:
  - Default filters: `from` is 30 days ago, `to` is today, `projectId` is ""
  - `setFrom`/`setTo`/`setProjectId` update the respective fields
  - `resetFilters` restores defaults
  - Filters persist to localStorage (verify by reading the storage key)
  **Acceptance**: All tests pass.

## Verification

- [ ] `npm run typecheck` passes in `client/` (no TS errors)
- [ ] `npm run lint` passes in `client/` (no lint errors)
- [ ] `npm test` passes all existing tests (no regressions)
- [ ] `npm test` passes all new analytics tests
- [ ] Manual verification: navigate to `/analytics`, see 4 tabs with placeholder/loading states
- [ ] Manual verification (with backend running): `/analytics` shows real data from analytics API
- [ ] Manual verification: date range filter changes reflected across all tabs
- [ ] Manual verification: sidebar shows BarChart3 icon, highlights on `/analytics`
- [ ] Manual verification: page works on mobile viewport (< 640px)
- [ ] Manual verification: charts render correctly in dark, light, and black themes
