<script setup lang="ts">
import { computed, inject } from "vue";
import ConversationPage from "@/components/session/ConversationPage.vue";
import { MOD_TREE } from "@/components/mods/mod-context";
import { str, type ModPageNode } from "@/components/mods/mod-style";
import { apiUrlOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import { modPageUrl } from "@/lib/mods/page-url";
import type { ModJson } from "@/lib/mods/types";

/**
 * A page the mod ships, in the conversation's sandboxed frame (opaque origin, Fleet's theme, sized to fit). Its address
 * is built from the mod that owns it (see `modPageUrl`); one that can't be built draws as a quiet line.
 */
const props = defineProps<{ node: ModPageNode }>();

const context = inject(MOD_TREE);
const machine = useMachineTarget();
const path = computed(() => str(props.node.props.path) ?? "");
const title = computed(() => str(props.node.props.title) ?? path.value);
const src = computed(() =>
  modPageUrl({
    apiUrl: (address) => apiUrlOn(machine.connection, address),
    sessionId: context?.sessionId ?? "",
    mod: props.node.mod,
    path: path.value,
    query: props.node.props.query as Record<string, ModJson> | undefined,
  }),
);
</script>

<template>
  <ConversationPage
    v-if="src"
    :src="src"
    :title="title"
    :page="{ path: path, id: `mod:${src}`, source: path }"
    contained
  />
  <span
    v-else
    class="mod-page--unavailable"
  >{{ title }}: Page unavailable</span>
</template>

<style scoped>
.mod-page--unavailable {
  color: var(--muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}
</style>
