<script setup lang="ts">
import { computed } from "vue";
import { ErrorNote } from "@/components/ui/error-note";
import { reportRenderError } from "@/lib/render-errors";

// What a route shows when it throws while loading or rendering (the router's defaultErrorComponent). It logs the
// error as it appears, since the router's boundary catches it and nothing else would. The crawl looks for
// data-fleet-error-fallback and fails a screen that shows it.
const props = defineProps<{
  error: unknown;
  reset?: () => void;
  /** The router passes it on a loader's error; declared so it isn't set on the page as an attribute. */
  info?: unknown;
}>();

reportRenderError(props.error, typeof window === "undefined" ? "" : window.location.pathname + window.location.search);

const reason = computed(() => {
  const error = props.error;
  const text = error instanceof Error ? error.message : String(error ?? "");
  return text || null;
});

function tryAgain(): void {
  props.reset?.();
}
</script>

<template>
  <section
    data-fleet-error-fallback
    class="flex h-full items-center justify-center p-6"
  >
    <ErrorNote
      class="w-full max-w-md"
      message="This page stopped with an error."
      :reason="reason"
      @retry="tryAgain"
    />
  </section>
</template>
