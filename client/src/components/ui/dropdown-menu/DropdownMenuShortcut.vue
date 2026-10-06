<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"
import { menuShortcutClass } from "@/components/ui/menu-classes"

const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
</script>

<template>
  <span
    data-slot="dropdown-menu-shortcut"
    :class="cn(menuShortcutClass, props.class)"
  >
    <slot />
  </span>
</template>
