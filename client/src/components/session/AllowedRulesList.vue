<script setup lang="ts">
import { computed } from "vue";
import { Button } from "@/components/ui/button";
import { formatRelativeTime } from "@/lib/format-utils";
import { allowedRuleWording, type AllowedRule } from "@/lib/tools";
import type { PermissionLevel } from "@/lib/permissions";

defineOptions({
  name: "AllowedRulesList",
});

/**
 * What "Don't ask again" allowed in a session, one row per click, each in plain words with when and a Remove.
 * With "Allow everything" nothing asks, so the rows are greyed and a line says they count again when asking comes back.
 */
const props = defineProps<{
  rules: readonly AllowedRule[];
  level: PermissionLevel;
}>();

const emit = defineEmits<{
  remove: [ruleId: string];
  removeAll: [];
}>();

const dormant = computed(() => props.level === "all");
const rows = computed(() => props.rules.map((rule) => ({ rule, words: allowedRuleWording(rule) })));
</script>

<template>
  <div
    class="allowed"
    data-testid="allowed-list"
  >
    <p
      v-if="dormant"
      class="allowed__note"
      data-testid="allowed-dormant"
    >
      Not needed now: this session allows everything. They count again if you turn asking back on.
    </p>
    <ul
      class="allowed__rows"
      :class="{ 'allowed__rows--dormant': dormant }"
    >
      <li
        v-for="{ rule, words } in rows"
        :key="rule.id"
        class="allowed__row"
        :class="{ 'allowed__row--broad': words.broad }"
        data-testid="allowed-rule"
      >
        <span class="allowed__text">
          <span class="allowed__what">{{ words.lead }}<template v-if="words.code">
            {{ " " }}<code>{{ words.code }}</code>
          </template>{{ words.tail }}</span>
          <span class="allowed__when">{{ formatRelativeTime(rule.createdAt) }}</span>
        </span>
        <Button
          variant="ghost"
          size="sm"
          class="allowed__remove"
          data-testid="allowed-remove"
          @click="emit('remove', rule.id)"
        >
          Remove
        </Button>
      </li>
    </ul>
    <div
      v-if="rules.length > 1"
      class="allowed__footer"
    >
      <Button
        variant="ghost"
        size="sm"
        data-testid="allowed-remove-all"
        @click="emit('removeAll')"
      >
        Remove all
      </Button>
    </div>
  </div>
</template>

<style scoped>
.allowed { display: flex; flex-direction: column; gap: 0.5rem; }
.allowed__note { margin: 0; font-size: 0.75rem; line-height: 1.4; color: var(--muted-foreground); }
.allowed__rows { margin: 0; padding: 0; list-style: none; display: flex; flex-direction: column; }
.allowed__rows--dormant { opacity: 0.55; }
.allowed__row { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; padding: 0.4rem 0; }
.allowed__row + .allowed__row { border-top: 1px solid var(--border); }
.allowed__text { display: flex; flex-direction: column; min-width: 0; gap: 0.1rem; }
.allowed__what { font-size: 0.8125rem; overflow-wrap: anywhere; }
.allowed__what code { font-family: var(--font-mono, monospace); font-size: 0.75rem; }
.allowed__row--broad .allowed__what { color: var(--warning, #b45309); }
.allowed__when { font-size: 0.6875rem; color: var(--muted-foreground); }
.allowed__footer { display: flex; justify-content: flex-end; border-top: 1px solid var(--border); padding-top: 0.25rem; }
</style>
