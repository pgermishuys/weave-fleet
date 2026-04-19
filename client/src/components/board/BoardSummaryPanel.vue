<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useFleetSummary } from "@/composables/use-fleet-summary";
import { formatAnalyticsCost, formatLargeNumber } from "@/lib/format-utils";
import { boardStatusOptions, useBoardStore } from "@/stores/board";

interface AgentSummary {
  agent: string;
  sessions: number;
  cost: number;
  tokens: number;
}

const boardStore = useBoardStore();
const { sessions, filteredSessions } = storeToRefs(boardStore);
const { summary, isLoading, error } = useFleetSummary();

const totalSessionsCount = computed(() => sessions.value.length);
const visibleSessionsCount = computed(() => filteredSessions.value.length);

const visibleCostTotal = computed(() => {
  return filteredSessions.value.reduce((total, session) => total + session.cost, 0);
});

const visibleTokensTotal = computed(() => {
  return filteredSessions.value.reduce((total, session) => total + session.totalTokens, 0);
});

const averageVisibleCost = computed(() => {
  if (visibleSessionsCount.value === 0) {
    return 0;
  }

  return visibleCostTotal.value / visibleSessionsCount.value;
});

const statusStats = computed(() => {
  return boardStatusOptions.map((status) => {
    const count = filteredSessions.value.filter((session) => session.status === status.value).length;
    const share = visibleSessionsCount.value === 0 ? 0 : count / visibleSessionsCount.value;

    return {
      ...status,
      count,
      share,
      width: `${Math.max(share * 100, count > 0 ? 6 : 0)}%`,
    };
  });
});

const agentStats = computed<AgentSummary[]>(() => {
  const summaries = new Map<string, AgentSummary>();

  for (const session of filteredSessions.value) {
    const existing = summaries.get(session.agent) ?? {
      agent: session.agent,
      sessions: 0,
      cost: 0,
      tokens: 0,
    };

    existing.sessions += 1;
    existing.cost += session.cost;
    existing.tokens += session.totalTokens;
    summaries.set(session.agent, existing);
  }

  return [...summaries.values()].sort((left, right) => {
    if (right.sessions !== left.sessions) {
      return right.sessions - left.sessions;
    }

    if (right.cost !== left.cost) {
      return right.cost - left.cost;
    }

    return left.agent.localeCompare(right.agent);
  });
});

const spendSegments = computed(() => {
  if (visibleCostTotal.value <= 0) {
    return agentStats.value.map((agent) => ({
      ...agent,
      width: `${visibleSessionsCount.value === 0 ? 0 : (agent.sessions / visibleSessionsCount.value) * 100}%`,
    }));
  }

  return agentStats.value.map((agent) => ({
    ...agent,
    width: `${(agent.cost / visibleCostTotal.value) * 100}%`,
  }));
});

const queuedTasksCount = computed(() => {
  return summary.value?.queuedTasks;
});

const liveFleetSummaryLabel = computed(() => {
  if (!summary.value) {
    return null;
  }

  return `${summary.value.activeSessions} active • ${summary.value.idleSessions} idle from fleet summary`;
});
</script>

<template>
  <section class="board-summary" aria-label="Board summary panel">
    <article class="summary-card">
      <p class="summary-big">
        {{ totalSessionsCount }}
      </p>
      <p class="summary-label">
        Total sessions
      </p>
      <p class="summary-meta">
        {{ visibleSessionsCount }} visible with current board filters
      </p>
    </article>

    <article class="summary-card">
      <h3 class="section-title">
        Status breakdown
      </h3>

      <div
        v-for="status in statusStats"
        :key="status.value"
        class="bar-row"
      >
        <span class="bar-label">{{ status.label }}</span>
        <span class="bar-value">{{ status.count }}</span>
        <div class="bar-track" aria-hidden="true">
          <div
            class="bar-fill"
            :style="{ width: status.width, backgroundColor: status.color }"
          />
        </div>
        <span class="bar-percent">{{ Math.round(status.share * 100) }}%</span>
      </div>
    </article>

    <article class="summary-card">
      <h3 class="section-title">
        Spend by agent
      </h3>

      <div class="stacked-bar" aria-label="Visible session spend distribution">
        <div
          v-for="agent in spendSegments"
          :key="agent.agent"
          class="stacked-segment"
          :style="{ width: agent.width }"
          :title="`${agent.agent}: ${formatAnalyticsCost(agent.cost)}`"
        />
      </div>

      <div class="agents-list" aria-label="Agent session counts">
        <div
          v-for="agent in agentStats"
          :key="agent.agent"
          class="agent-row"
        >
          <div class="agent-copy">
            <span class="agent-name">{{ agent.agent }}</span>
            <span class="agent-detail">{{ formatLargeNumber(agent.tokens) }} tokens</span>
          </div>
          <div class="agent-stats">
            <span class="agent-count">{{ agent.sessions }} sessions</span>
            <span class="agent-cost">{{ formatAnalyticsCost(agent.cost) }}</span>
          </div>
        </div>
      </div>
    </article>

    <article class="summary-card">
      <h3 class="section-title">
        Cost breakdown
      </h3>

      <div class="metric-row">
        <span class="metric-label">Visible session cost</span>
        <span class="metric-value">{{ formatAnalyticsCost(visibleCostTotal) }}</span>
      </div>
      <div class="metric-row">
        <span class="metric-label">Average per visible session</span>
        <span class="metric-value">{{ formatAnalyticsCost(averageVisibleCost) }}</span>
      </div>
      <div class="metric-row">
        <span class="metric-label">Visible session tokens</span>
        <span class="metric-value">{{ formatLargeNumber(visibleTokensTotal) }}</span>
      </div>
      <div v-if="queuedTasksCount !== undefined" class="metric-row">
        <span class="metric-label">Queued tasks</span>
        <span class="metric-value">{{ queuedTasksCount }}</span>
      </div>

      <p v-if="liveFleetSummaryLabel" class="summary-footnote">
        {{ liveFleetSummaryLabel }}
      </p>
      <p v-else-if="isLoading" class="summary-footnote">
        Loading live fleet summary…
      </p>
      <p v-else-if="error" class="summary-footnote summary-footnote--error">
        Live fleet summary unavailable: {{ error }}
      </p>
    </article>
  </section>
</template>

<style scoped>
.board-summary {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.summary-card {
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.02);
}

.summary-big {
  margin: 0 0 4px;
  font-size: 28px;
  font-weight: 700;
  color: var(--text);
}

.summary-label {
  margin: 0 0 14px;
  font-size: 11px;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.summary-meta,
.summary-footnote {
  margin: 0;
  font-size: 11px;
  line-height: 1.5;
  color: var(--muted);
}

.summary-footnote {
  margin-top: 10px;
}

.summary-footnote--error {
  color: #fca5a5;
}

.section-title {
  margin: 0 0 12px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.bar-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  font-size: 11px;
}

.bar-row:last-child {
  margin-bottom: 0;
}

.bar-label,
.bar-value,
.bar-percent,
.metric-label,
.agent-detail,
.agent-count {
  color: var(--muted);
}

.bar-label {
  width: 46px;
}

.bar-value,
.bar-percent {
  width: 30px;
  text-align: right;
}

.bar-track {
  flex: 1;
  height: 8px;
  background: rgba(255, 255, 255, 0.04);
  border-radius: 4px;
  overflow: hidden;
}

.bar-fill {
  height: 100%;
  border-radius: 4px;
}

.stacked-bar {
  display: flex;
  height: 12px;
  margin-bottom: 14px;
  border-radius: 6px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.04);
}

.stacked-segment:nth-child(5n + 1) {
  background: var(--accent);
}

.stacked-segment:nth-child(5n + 2) {
  background: var(--running);
}

.stacked-segment:nth-child(5n + 3) {
  background: #f59e0b;
}

.stacked-segment:nth-child(5n + 4) {
  background: var(--complete);
}

.stacked-segment:nth-child(5n + 5) {
  background: #a855f7;
}

.agents-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.agent-row,
.metric-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.agent-copy,
.agent-stats {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.agent-stats {
  align-items: flex-end;
}

.agent-name,
.agent-cost,
.metric-value {
  color: var(--text);
}

.agent-name,
.metric-value {
  font-size: 12px;
  font-weight: 600;
}

.agent-detail,
.agent-count,
.agent-cost,
.metric-label {
  font-size: 11px;
}

.metric-row {
  padding: 6px 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
}

.metric-row:last-of-type {
  border-bottom: 0;
}
</style>
