<script setup lang="ts">
import { ArrowLeft } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PageCanvas from "@/components/canvas/PageCanvas.vue";
import type { ShownPage } from "@/lib/server-canvas";

/** The page the agent waits on, full height, with the same Send bar as the desktop (PageCanvas). ← goes back to the conversation. */
defineProps<{ open: boolean; sessionId: string; page: (ShownPage & { title: string }) | null }>();
const emit = defineEmits<{ (event: "close"): void }>();
</script>

<template>
  <BottomSheet
    :open="open"
    label="Page"
    :detents="['large']"
    bare
    @close="emit('close')"
  >
    <div class="pps">
      <header class="pps__head">
        <button
          type="button"
          class="ph-icon-btn"
          aria-label="Back"
          data-testid="page-pick-back"
          @click="emit('close')"
        >
          <ArrowLeft aria-hidden="true" />
        </button>
        <h2 class="pps__title">
          {{ page?.title }}
        </h2>
      </header>
      <PageCanvas
        v-if="page"
        :session-id="sessionId"
        :page="page"
        compact
      />
    </div>
  </BottomSheet>
</template>

<style scoped>
.pps {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.pps__head {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
  padding: 0 8px;
  border-bottom: 1px solid var(--border);
}

.pps__title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}
</style>
