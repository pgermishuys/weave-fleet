<script setup lang="ts">
import { computed } from 'vue'
import { sanitizeHtml } from '@/lib/sanitize-html'

interface Props {
  content: string
}

const props = defineProps<Props>()

const sanitizedContent = computed(() => sanitizeHtml(props.content))
</script>

<template>
  <div v-html="sanitizedContent" />
</template>
