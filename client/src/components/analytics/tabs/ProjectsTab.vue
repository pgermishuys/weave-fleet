<script setup lang="ts">
import { computed } from "vue";
import { Card, CardContent } from "@/components/ui/card";
import type { AnalyticsProjectOption } from "@/composables/use-analytics-filters";
import { formatAnalyticsCost } from "@/lib/format-utils";

interface ProjectCard extends AnalyticsProjectOption {
  relativeCostWidth: number;
  formattedTokens: string;
  formattedCost: string;
}

interface Props {
  topProjects: readonly AnalyticsProjectOption[];
  emptyMessage?: string;
}

const props = withDefaults(defineProps<Props>(), {
  emptyMessage: "No project analytics available for the selected filters.",
});

const integerFormatter = new Intl.NumberFormat("en-US");

const maxCost = computed(() => {
  return props.topProjects.reduce((highest, project) => Math.max(highest, project.cost), 0);
});

const projectCards = computed<ProjectCard[]>(() => {
  return [...props.topProjects]
    .sort((left, right) => right.cost - left.cost || left.name.localeCompare(right.name))
    .map((project) => ({
      ...project,
      relativeCostWidth: getRelativeCostWidth(project.cost, maxCost.value),
      formattedTokens: integerFormatter.format(project.tokens),
      formattedCost: formatAnalyticsCost(project.cost),
    }));
});

function getRelativeCostWidth(cost: number, highestCost: number): number {
  if (!Number.isFinite(cost) || !Number.isFinite(highestCost) || cost <= 0 || highestCost <= 0) {
    return 0;
  }

  return Math.min((cost / highestCost) * 100, 100);
}
</script>

<template>
  <section
    class="projects-tab"
    aria-label="Projects analytics"
  >
    <div
      v-if="projectCards.length > 0"
      class="projects-tab__grid"
    >
      <Card
        v-for="project in projectCards"
        :key="project.id"
        class="projects-tab__card border-border/80 bg-card/70 py-0 backdrop-blur-sm"
      >
        <CardContent class="projects-tab__content">
          <div class="projects-tab__header">
            <div class="projects-tab__title-block">
              <p class="projects-tab__eyebrow">
                Project
              </p>
              <h3 class="projects-tab__title">
                {{ project.name }}
              </h3>
            </div>

            <p class="projects-tab__cost">
              {{ project.formattedCost }}
            </p>
          </div>

          <dl class="projects-tab__stats">
            <div class="projects-tab__stat">
              <dt class="projects-tab__stat-label">
                Tokens
              </dt>
              <dd class="projects-tab__stat-value">
                {{ project.formattedTokens }}
              </dd>
            </div>

            <div class="projects-tab__stat">
              <dt class="projects-tab__stat-label">
                Cost
              </dt>
              <dd class="projects-tab__stat-value">
                {{ project.formattedCost }}
              </dd>
            </div>
          </dl>

          <div class="projects-tab__bar-group">
            <div class="projects-tab__bar-labels">
              <span class="projects-tab__bar-label">Relative spend</span>
              <span class="projects-tab__bar-value">{{ project.relativeCostWidth.toFixed(0) }}%</span>
            </div>

            <div
              class="projects-tab__bar-track"
              role="img"
              :aria-label="`${project.name} relative cost ${project.relativeCostWidth.toFixed(0)} percent`"
            >
              <div
                class="projects-tab__bar-fill"
                :style="{ width: `${project.relativeCostWidth}%` }"
              />
            </div>
          </div>
        </CardContent>
      </Card>
    </div>

    <div
      v-else
      class="projects-tab__empty"
    >
      <p class="projects-tab__empty-text">
        {{ props.emptyMessage }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.projects-tab {
  width: 100%;
}

.projects-tab__grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 16px;
}

.projects-tab__card {
  height: 100%;
}

.projects-tab__content {
  display: flex;
  height: 100%;
  flex-direction: column;
  gap: 20px;
  padding: 20px;
}

.projects-tab__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.projects-tab__title-block {
  min-width: 0;
}

.projects-tab__eyebrow {
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.projects-tab__title {
  margin-top: 6px;
  color: var(--text);
  font-size: 1.05rem;
  font-weight: 600;
  line-height: 1.35;
  overflow-wrap: anywhere;
}

.projects-tab__cost {
  flex-shrink: 0;
  color: var(--text);
  font-size: 1rem;
  font-weight: 600;
  line-height: 1.4;
  text-align: right;
}

.projects-tab__stats {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

.projects-tab__stat {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.projects-tab__stat-label {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.projects-tab__stat-value {
  color: var(--text);
  font-size: 1.05rem;
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  line-height: 1.4;
}

.projects-tab__bar-group {
  margin-top: auto;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.projects-tab__bar-labels {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.projects-tab__bar-label,
.projects-tab__bar-value {
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  line-height: 1.4;
}

.projects-tab__bar-value {
  font-variant-numeric: tabular-nums;
}

.projects-tab__bar-track {
  height: 10px;
  overflow: hidden;
  border-radius: 999px;
  background: color-mix(in srgb, var(--muted) 18%, transparent);
}

.projects-tab__bar-fill {
  height: 100%;
  border-radius: 999px;
  background: linear-gradient(90deg, rgba(99, 102, 241, 0.92), rgba(34, 197, 94, 0.8));
  transition: width 0.2s ease;
}

.projects-tab__empty {
  display: flex;
  min-height: 180px;
  align-items: center;
  justify-content: center;
  border: 1px solid color-mix(in srgb, var(--border) 80%, transparent);
  border-radius: 16px;
  background: color-mix(in srgb, var(--card-bg) 70%, transparent);
  padding: 24px;
  text-align: center;
}

.projects-tab__empty-text {
  max-width: 32rem;
  color: var(--muted);
  font-size: 0.95rem;
  line-height: 1.6;
}

@media (max-width: 640px) {
  .projects-tab__stats {
    grid-template-columns: 1fr;
  }

  .projects-tab__header {
    flex-direction: column;
  }

  .projects-tab__cost {
    text-align: left;
  }
}
</style>
