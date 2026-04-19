<script setup lang="ts">
import type { BoardSession } from "@/stores/board";
import { computed } from "vue";
import { formatCost, formatDuration, formatTokens } from "@/lib/format-utils";
import { getBoardStatusMeta } from "@/stores/board";

const props = defineProps<{
  session: BoardSession;
}>();

const statusMeta = computed(() => getBoardStatusMeta(props.session.status));

const cardClassName = computed(() => ({
  "failed-card": props.session.status === "error",
}));

const cardStyle = computed(() => ({
  borderLeftColor: statusMeta.value.color,
}));

const progressStyle = computed(() => ({
  width: `${props.session.progressPercent}%`,
  backgroundColor: statusMeta.value.color,
}));
</script>

<template>
  <article class="k-card" :class="cardClassName" :style="cardStyle">
    <div class="k-card__header">
      <h3 class="k-card__title">
        {{ session.title }}
      </h3>
      <span class="k-card__status">
        {{ statusMeta.label }}
      </span>
    </div>

    <div class="k-card__project-row">
      <span class="k-card__project-dot" :style="{ backgroundColor: session.projectColor }" aria-hidden="true" />
      <span class="k-card__project-name">{{ session.projectName }}</span>
    </div>

    <div class="k-card__pills">
      <span class="k-card__pill">
        {{ session.agent }}
      </span>
      <span class="k-card__pill k-card__pill--muted">
        {{ session.modelName }}
      </span>
    </div>

    <div class="k-card__progress-meta">
      <span>{{ session.progressLabel }}</span>
      <span>{{ session.progressPercent }}%</span>
    </div>

    <dl class="k-card__footer">
      <div class="k-card__stat">
        <dt>Tokens</dt>
        <dd>{{ formatTokens(session.totalTokens) }}</dd>
      </div>

      <div class="k-card__stat">
        <dt>Time</dt>
        <dd>{{ formatDuration(session.durationSeconds) }}</dd>
      </div>

      <div class="k-card__stat">
        <dt>Cost</dt>
        <dd>{{ formatCost(session.cost) }}</dd>
      </div>
    </dl>

    <div class="k-card__progress-track" aria-hidden="true">
      <div class="k-progress" :style="progressStyle" />
    </div>
  </article>
</template>

<style scoped>
.k-card {
  position: relative;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  padding: 12px;
  padding-bottom: 16px;
  border-left: 4px solid var(--muted);
  cursor: pointer;
  transition: transform 0.25s ease, border-color 0.25s ease, box-shadow 0.25s ease;
  overflow: hidden;
}

.k-card:hover {
  transform: translateY(-4px);
  box-shadow: 0 16px 32px rgba(0, 0, 0, 0.22);
}

.k-card.failed-card {
  background: rgba(239, 68, 68, 0.04);
}

.k-card__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}

.k-card__title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.45;
  color: var(--text);
}

.k-card__status {
  flex: 0 0 auto;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.05);
  padding: 4px 8px;
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.k-card__project-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 10px;
}

.k-card__project-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: 0 0 auto;
}

.k-card__project-name {
  font-size: 12px;
  color: var(--muted);
}

.k-card__pills {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 10px;
}

.k-card__pill {
  display: inline-flex;
  align-items: center;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.04);
  padding: 4px 8px;
  font-size: 11px;
  font-weight: 500;
  color: var(--text);
}

.k-card__pill--muted {
  color: var(--muted);
}

.k-card__progress-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 12px;
  font-size: 11px;
  color: var(--muted);
}

.k-card__footer {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
  margin: 12px 0 0;
}

.k-card__stat {
  min-width: 0;
}

.k-card__stat dt,
.k-card__stat dd {
  margin: 0;
}

.k-card__stat dt {
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.k-card__stat dd {
  margin-top: 4px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.k-card__progress-track {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 2px;
  background: rgba(255, 255, 255, 0.05);
}

.k-progress {
  position: absolute;
  bottom: 0;
  left: 0;
  height: 2px;
}
</style>
