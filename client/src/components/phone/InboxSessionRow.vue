<script setup lang="ts">
import { computed } from "vue";
import PhoneGlyph from "@/components/phone/PhoneGlyph.vue";
import { rowGlyph, rowMeta, rowPlace, type InboxItem } from "@/lib/phone/inbox";
import { duration, short } from "@/lib/phone/time";

/**
 * A session as a row, as the desktop's session list draws it: its status glyph, the title, where it is and its
 * branch under it, and on the right "Needs you", how long it's been working or how long ago it finished. Tapping
 * opens it.
 */
const props = withDefaults(defineProps<{ item: InboxItem; now: number; by?: "machine" | "folder" }>(), { by: "machine" });
const emit = defineEmits<{ (event: "open", item: InboxItem): void }>();

const glyph = computed(() => rowGlyph(props.item));
const place = computed(() => rowPlace(props.item, props.by));
const meta = computed(() => rowMeta(props.item, { duration: duration(props.item.updatedAt, props.now), short: short(props.item.updatedAt, props.now) }));
</script>

<template>
  <button
    type="button"
    class="ph-srow"
    :class="{ 'ph-srow--quiet': glyph === 'quiet', 'ph-srow--stale': item.stale }"
    data-testid="inbox-row"
    @click="emit('open', item)"
  >
    <span class="ph-srow__g"><PhoneGlyph :kind="glyph" /></span>
    <span class="ph-srow__main">
      <span class="ph-srow__t">{{ item.title }}</span>
      <span class="ph-srow__s">{{ place }}</span>
    </span>
    <span
      class="ph-srow__m"
      :class="{ 'ph-srow__m--waiting': meta.tone === 'waiting', 'ph-srow__m--error': meta.tone === 'error' }"
    >{{ meta.text }}</span>
  </button>
</template>
