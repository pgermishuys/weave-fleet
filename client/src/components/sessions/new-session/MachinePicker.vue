<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown, Plus } from "lucide-vue-next";
import { DropdownMenuItem, DropdownMenuSeparator } from "reka-ui";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { formatRelativeTime } from "@/lib/format-utils";
import type { MachineEntry, MachineSessions } from "@/stores/machines";

/**
 * Which machine the session starts on. Machines get coral, as in the sidebar. One that can't be reached is listed but
 * can't be picked.
 */
const props = defineProps<{
  machines: readonly MachineEntry[];
  /** The machine the session starts on (a machine key). */
  selected: string;
  /** What the sidebar last heard from each machine that isn't live. */
  others: Readonly<Record<string, MachineSessions>>;
  /** Whether the live machine answers. */
  liveReachable: boolean;
  disabled?: boolean;
  /** The menu's heading: "Start on" for a session, "Runs on" for an automation. */
  heading?: string;
}>();

const emit = defineEmits<{
  "update:selected": [machineKey: string];
  addMachine: [];
  closeAutoFocus: [event: Event];
}>();

const OS_NAMES: Record<string, string> = { linux: "Linux", macos: "macOS", windows: "Windows" };

interface Row {
  entry: MachineEntry;
  reachable: boolean;
  detail: string;
}

const rows = computed<Row[]>(() => props.machines.map((entry) => {
  const os = entry.os ? OS_NAMES[entry.os] ?? null : null;
  if (entry.isLive) {
    const reachable = props.liveReachable;
    return {
      entry,
      reachable,
      detail: reachable ? ["Working here", os].filter(Boolean).join(" · ") : "Unreachable",
    };
  }
  const heard = props.others[entry.key];
  const reachable = !heard?.error;
  const when = heard?.loadedAt ? formatRelativeTime(heard.loadedAt) : null;
  return {
    entry,
    reachable,
    detail: reachable
      ? [os, when ? `reached ${when}` : null].filter(Boolean).join(" · ")
      : when ? `Unreachable · last seen ${when}` : "Unreachable",
  };
}));

const selectedName = computed(() =>
  props.machines.find((entry) => entry.key === props.selected)?.name ?? props.machines[0]?.name ?? "");
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip ns-machine-chip"
        data-testid="new-session-machine"
        aria-label="Machine"
        :disabled="disabled"
      >
        <span
          class="ns-machine__dot"
          aria-hidden="true"
        />
        <span class="ns-chip__label ns-machine-chip__name">{{ selectedName }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>

    <DropdownMenuContent
      class="ns-pop ns-machine"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-pop__label">
        {{ heading ?? "Start on" }}
      </div>
      <DropdownMenuItem
        v-for="row in rows"
        :key="row.entry.key"
        class="ns-option"
        :disabled="!row.reachable"
        :data-testid="`new-session-machine-${row.entry.key}`"
        @select="emit('update:selected', row.entry.key)"
      >
        <span
          class="ns-machine__dot"
          :class="{ 'ns-machine__dot--down': !row.reachable }"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title ns-machine__name">{{ row.entry.name }}</span>
          <span class="ns-option__detail">{{ row.detail }}</span>
        </span>
        <Check
          v-if="row.entry.key === selected"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuSeparator class="ns-pop__separator" />
      <DropdownMenuItem
        class="ns-option"
        data-testid="new-session-machine-add"
        @select="emit('addMachine')"
      >
        <Plus
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Add a machine…</span>
          <span class="ns-option__detail">Settings → Machines</span>
        </span>
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style>
.ns-machine {
  width: 300px;
}

.ns-machine-chip .ns-machine-chip__name,
.ns-machine__name {
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.ns-machine-chip .ns-machine-chip__name {
  color: var(--coral);
}

.ns-machine__dot {
  width: 7px;
  height: 7px;
  flex-shrink: 0;
  justify-self: center;
  border-radius: 999px;
  background: var(--running);
}

.ns-machine__dot--down {
  background: var(--error);
}

.ns-option[data-disabled] {
  cursor: default;
  opacity: 0.55;
}
</style>
