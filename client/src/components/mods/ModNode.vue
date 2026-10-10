<script setup lang="ts">
import { inject } from "vue";
import ModBox from "@/components/mods/ModBox.vue";
import ModButton from "@/components/mods/ModButton.vue";
import ModCode from "@/components/mods/ModCode.vue";
import ModIcon from "@/components/mods/ModIcon.vue";
import ModInput from "@/components/mods/ModInput.vue";
import ModMarkdown from "@/components/mods/ModMarkdown.vue";
import ModPage from "@/components/mods/ModPage.vue";
import ModPill from "@/components/mods/ModPill.vue";
import ModSelect from "@/components/mods/ModSelect.vue";
import ModText from "@/components/mods/ModText.vue";
import { MOD_TREE } from "@/components/mods/mod-context";
import type { ModWireElement } from "@/lib/mods/types";

/** One node of a mod's tree: a string is text, an element goes to the component that draws it. */
defineProps<{ node: ModWireElement | string }>();

const context = inject(MOD_TREE);
// A `Fleet` node is the host's own drawing of the site; with none given it draws nothing.
const FleetSlot = () => context?.drawFleet();
</script>

<template>
  <template v-if="typeof node === 'string'">
    {{ node }}
  </template>
  <ModBox
    v-else-if="node.type === 'Box'"
    :node="node"
  />
  <ModText
    v-else-if="node.type === 'Text'"
    :node="node"
  />
  <ModPill
    v-else-if="node.type === 'Pill'"
    :node="node"
  />
  <ModIcon
    v-else-if="node.type === 'Icon'"
    :node="node"
  />
  <ModButton
    v-else-if="node.type === 'Button'"
    :node="node"
  />
  <ModInput
    v-else-if="node.type === 'Input'"
    :node="node"
  />
  <ModSelect
    v-else-if="node.type === 'Select'"
    :node="node"
  />
  <ModMarkdown
    v-else-if="node.type === 'Markdown'"
    :node="node"
  />
  <ModCode
    v-else-if="node.type === 'Code'"
    :node="node"
  />
  <ModPage
    v-else-if="node.type === 'Page'"
    :node="node"
  />
  <FleetSlot v-else-if="node.type === 'Fleet'" />
</template>
