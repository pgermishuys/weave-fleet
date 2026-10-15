<script setup lang="ts">
import { Compass, Ellipsis, EyeOff, ListChecks, Search, Workflow } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { BUILT_IN_STARTERS, type TextStarter } from "@/lib/starters";

defineProps<{
  /** Fix a bug, when Workflows is on. */
  showWorkflow: boolean;
}>();

const emit = defineEmits<{
  fill: [starter: TextStarter];
  openWorkflow: [];
  hide: [];
  closeAutoFocus: [event: Event];
}>();

const icons = { explore: Compass, plan: ListChecks, review: Search } as const;
</script>

<template>
  <div
    class="ns-starters"
    role="group"
    aria-label="Starters"
    data-testid="new-session-starters"
  >
    <button
      v-for="starter in BUILT_IN_STARTERS"
      :key="starter.id"
      type="button"
      class="ns-starter"
      :data-testid="`new-session-starter-${starter.id}`"
      title="Fills the box. Sends nothing."
      @click="emit('fill', starter)"
    >
      <component
        :is="icons[starter.id]"
        class="ns-starter__icon"
        aria-hidden="true"
      />
      {{ starter.label }}
    </button>
    <button
      v-if="showWorkflow"
      type="button"
      class="ns-starter"
      data-testid="new-session-starter-fix-a-bug"
      title="Opens the workflow's Run box. Starts nothing."
      @click="emit('openWorkflow')"
    >
      <Workflow
        class="ns-starter__icon"
        aria-hidden="true"
      />
      Fix a bug
      <span class="ns-starter__tag">workflow</span>
    </button>

    <DropdownMenu :modal="false">
      <DropdownMenuTrigger as-child>
        <button
          type="button"
          class="ns-starter ns-starter--menu"
          data-testid="new-session-starters-menu"
          aria-label="Starter options"
        >
          <Ellipsis
            class="ns-starter__icon"
            aria-hidden="true"
          />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        class="ns-pop ns-starters__pop"
        side="bottom"
        align="end"
        :side-offset="6"
        :collision-padding="8"
        @close-auto-focus="emit('closeAutoFocus', $event)"
      >
        <DropdownMenuItem
          class="ns-option"
          data-testid="new-session-starters-hide"
          @select="emit('hide')"
        >
          <EyeOff
            class="ns-option__icon"
            aria-hidden="true"
          />
          <span class="ns-option__text">Hide starters</span>
        </DropdownMenuItem>
        <p class="ns-pop__note">
          Settings → Features → Show starters brings them back.
        </p>
      </DropdownMenuContent>
    </DropdownMenu>
  </div>
</template>

<style scoped>
/* One quiet line. When it doesn't fit (a phone) it scrolls sideways instead of wrapping. */
.ns-starters {
  display: flex;
  max-width: 100%;
  gap: 6px;
  margin-top: 16px;
  overflow-x: auto;
  scrollbar-width: none;
}

.ns-starters::-webkit-scrollbar {
  display: none;
}

/* Centred while it fits; from the start once it scrolls, so the first chip is never cut off. */
.ns-starters > :first-child {
  margin-left: auto;
}

.ns-starters > :last-child {
  margin-right: auto;
}

.ns-starter {
  display: inline-flex;
  height: 26px;
  flex-shrink: 0;
  align-items: center;
  gap: 5px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 12.5px;
  white-space: nowrap;
  transition: background var(--transition), color var(--transition);
}

.ns-starter:hover,
.ns-starter[data-state="open"] {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.ns-starter:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.ns-starter--menu {
  padding: 0 8px;
  border-color: transparent;
}

.ns-starter__icon {
  width: 12px;
  height: 12px;
  opacity: 0.7;
}

.ns-starter__tag {
  margin-left: 2px;
  padding: 0 4px;
  border: 1px solid var(--border);
  border-radius: 4px;
  color: var(--muted);
  font-size: 10.5px;
}

.ns-starters__pop {
  width: 260px;
}

@media (max-width: 767px) {
  .ns-starter {
    height: 32px;
  }
}
</style>
