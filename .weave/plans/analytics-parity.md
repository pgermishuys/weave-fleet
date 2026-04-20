# Analytics View Parity Spec

## TL;DR
> **Summary**: Reintroduce tabbed analytics (Overview, Projects, Sessions, Models) matching legacy content/detail parity while preserving the current Vue visual language.
> **Estimated Effort**: Medium

## 1. Gap Analysis

| Aspect | Legacy (Next.js) | Current (Vue) | Gap |
|--------|-----------------|---------------|-----|
| **Tabs** | Overview, Projects, Sessions, Models | Single flat page | No tab navigation |
| **Project filter** | Dropdown populated from `summary.topProjects` | Freeform text input with datalist from sessions | Should use `topProjects` from summary |
| **Summary cards** | Tokens, Cost + estimated secondary, Sessions, Messages | Sessions, Cost, Tokens, Top Session | Missing: messages card, estimated cost secondary, wrong 4th card |
| **Estimated cost** | Shown as secondary on cost card; available on daily/session/model rows | Not surfaced anywhere | `totalEstimatedCost`, `estimatedCost` fields unused |
| **Daily chart** | Dual-axis: tokens + cost | Dual-axis: sessions + cost | Missing tokens axis; sessions not in legacy overview chart |
| **Top models (overview)** | Cost-ranked list/bars | Token-ranked bar chart + cost list | Should rank by cost with relative bars |
| **Top projects (overview)** | Cost-ranked list/bars from `topProjects` | Not present | Entirely missing |
| **Projects tab** | Per-project cards: tokens, cost, relative cost bar | Not present | Entirely missing |
| **Sessions tab** | Full sortable table: title, project, tokens, cost, duration, models, created | Top-5 doughnut + short list | No table, no sorting, limited to 5, no `createdAt` |
| **Models tab** | Horizontal cost-by-model chart + detail table (model, provider, tokens, cost, messages, avg/msg) | Vertical token bar + simple list | Wrong chart orientation, missing avg/msg, missing provider column |
| **Session limit** | All sessions (paginated or full list) | Hard-coded `limit=5` | Need unlimited fetch or pagination |

## 2. Recommended Information Architecture

```
AnalyticsPage
├─ Header (title, subtitle)
├─ Filters bar (from, to, projectId dropdown from topProjects, reset)
├─ Tabs: [Overview | Projects | Sessions | Models]
│
├─ Overview tab
│   ├─ Stat cards row: Total Tokens, Total Cost (est. secondary), Sessions, Messages
│   ├─ Daily dual-axis line chart: Tokens (left) + Cost (right)
│   ├─ Top Models — horizontal cost bars (top 5)
│   └─ Top Projects — horizontal cost bars (top 5)
│
├─ Projects tab
│   └─ Grid of project cards: name, tokens, cost, relative cost bar
│
├─ Sessions tab
│   └─ Sortable table: title, project, tokens, cost, estimated cost, duration, models, createdAt
│
└─ Models tab
    ├─ Horizontal bar chart: cost by model
    └─ Detail table: model, provider, tokens, cost, estimated cost, messages, avg cost/msg
```

## 3. Proposed Component Map

```
src/components/analytics/
├─ AnalyticsPage.vue              (shell: header, filters, tab switcher, slot for active tab)
├─ AnalyticsFilters.vue           (from/to date pickers, project dropdown, reset)
├─ AnalyticsTabs.vue              (tab bar UI, emits active tab)
├─ tabs/
│   ├─ OverviewTab.vue            (stat cards, daily chart, top models, top projects)
│   ├─ ProjectsTab.vue            (project cards grid)
│   ├─ SessionsTab.vue            (sortable sessions table)
│   └─ ModelsTab.vue              (horizontal chart + detail table)
├─ cards/
│   └─ StatCard.vue               (label, value, secondary, detail)
├─ charts/
│   ├─ DailyTrendChart.vue        (dual-axis line: tokens + cost)
│   ├─ HorizontalCostBars.vue     (reusable horizontal bar list for models/projects)
│   └─ ModelCostChart.vue         (horizontal bar chart for models tab)
└─ tables/
    ├─ SessionsTable.vue          (sortable, all columns)
    └─ ModelsTable.vue            (all columns incl. avg/msg)

src/composables/
├─ use-analytics-filters.ts       (EXISTING — enhance: expose topProjects for dropdown)
├─ use-analytics-summary.ts       (EXISTING — no changes)
├─ use-analytics-daily.ts         (EXISTING — no changes)
├─ use-analytics-sessions.ts      (EXISTING — remove hard limit, add sort param)
└─ use-analytics-models.ts        (EXISTING — add projectId param)
```

## 4. Implementation Phases

### Phase 1: Composable adjustments
- [x] **use-analytics-sessions**: Remove hard `limit=5`, add optional `sortBy`/`sortDir` params
- [x] **use-analytics-models**: Add optional `projectId` param (parity with legacy)
- [x] **use-analytics-filters**: Expose `topProjects` from summary for dropdown population

### Phase 2: Shared UI atoms
- [x] **StatCard.vue**: Accepts `label`, `value`, `secondary` (for estimated cost), `detail`
- [x] **HorizontalCostBars.vue**: Accepts items array `{name, cost, maxCost}`, renders relative-width bars
- [x] **AnalyticsFilters.vue**: Extract filter bar; project dropdown populated from `topProjects`
- [x] **AnalyticsTabs.vue**: Simple tab bar component

### Phase 3: Tab views
- [x] **OverviewTab.vue**: 4 stat cards (tokens, cost+est, sessions, messages), daily dual-axis chart (tokens+cost), top models bars, top projects bars
- [x] **ProjectsTab.vue**: Grid of cards from `topProjects` with tokens, cost, relative bar
- [x] **SessionsTab.vue**: Full sortable table with all session fields including `estimatedCost` and `createdAt`
- [x] **ModelsTab.vue**: Horizontal cost chart + detail table with provider, tokens, cost, estimatedCost, messages, avgCostPerMessage

### Phase 4: Page assembly
- [x] **AnalyticsPage.vue**: Rewrite to thin shell — header, filters, tabs, dynamic tab content via `<component :is>`
- [x] Remove legacy single-page chart code
- [x] Ensure existing route (`/analytics`) still works unchanged

## 5. Acceptance Criteria

| # | Criterion |
|---|-----------|
| 1 | Page renders 4 tabs: Overview, Projects, Sessions, Models |
| 2 | Project filter is a dropdown populated from `summary.topProjects`, not freeform text |
| 3 | Overview stat cards show: Total Tokens, Total Cost with estimated cost secondary, Session Count, Message Count |
| 4 | Overview daily chart has dual axes: tokens (left) and cost (right), NOT sessions |
| 5 | Overview shows top models ranked by cost with horizontal relative bars |
| 6 | Overview shows top projects ranked by cost with horizontal relative bars |
| 7 | Projects tab renders a card per project with tokens, cost, and a relative cost bar |
| 8 | Sessions tab renders a table with columns: title, project, tokens, cost, estimated cost, duration, models, created — sortable by any numeric column |
| 9 | Sessions tab fetches all sessions (no hard limit of 5) |
| 10 | Models tab shows a horizontal bar chart of cost-by-model |
| 11 | Models tab detail table includes: model, provider, tokens, cost, estimated cost, messages, avg cost/msg |
| 12 | `estimatedCost` is surfaced wherever cost is shown (cards, tables, charts tooltips) |
| 13 | Filters (from, to, projectId) persist via localStorage with 30-day default (existing behavior preserved) |
| 14 | All existing composables continue to work; no API contract changes required |
| 15 | Visual language matches existing app: rounded cards, `--border`/`--surface` tokens, `--muted` text, `--radius-card` |

## Verification
- [x] `npm run build` passes with no type errors
- [x] All 4 tabs render with mock/real data
- [x] Filter changes propagate to all tabs
- [x] No regressions on other routes
