<script setup lang="ts">
import { PanelRightOpen } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";

/** The session header's right panel button where the panel is a sheet instead of a column (phones, narrow windows). */

const { rightPanelAsSheet, isRightPanelVisible, showRightPanel } = useSidebarMobile();
</script>

<template>
  <Button
    v-if="rightPanelAsSheet"
    variant="toolbar-icon"
    size="toolbar"
    data-testid="right-panel-sheet-toggle"
    aria-label="Open right panel"
    title="Open right panel"
    aria-haspopup="dialog"
    :aria-expanded="isRightPanelVisible"
    @click="showRightPanel"
  >
    <PanelRightOpen aria-hidden="true" />
  </Button>
</template>
