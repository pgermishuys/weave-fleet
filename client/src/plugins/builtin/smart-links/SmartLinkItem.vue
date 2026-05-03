<script setup lang="ts">
import { computed } from 'vue'
import {
  CircleDot,
  CircleCheck,
  CircleDashed,
  GitMerge,
  GitPullRequest,
  GitPullRequestClosed,
  X,
} from 'lucide-vue-next'
import type { SmartLink } from './types'

const props = defineProps<{ link: SmartLink }>()
const emit = defineEmits<{ dismiss: [linkId: string] }>()

const statusIcon = computed(() => {
  if (props.link.resourceType === 'pull_request') {
    switch (props.link.status) {
      case 'merged': return GitMerge
      case 'closed': return GitPullRequestClosed
      case 'draft': return GitPullRequest
      default: return GitPullRequest
    }
  }
  // Issues
  switch (props.link.status) {
    case 'closed': return CircleCheck
    case 'open': return CircleDot
    default: return CircleDashed
  }
})

const statusClass = computed(() => {
  switch (props.link.status) {
    case 'merged': return 'smart-link-icon smart-link-icon--merged'
    case 'closed': return 'smart-link-icon smart-link-icon--closed'
    case 'open': return 'smart-link-icon smart-link-icon--open'
    case 'draft': return 'smart-link-icon smart-link-icon--draft'
    default: return 'smart-link-icon'
  }
})

interface LinkLabel {
  name: string
  color: string
}

const labels = computed<LinkLabel[]>(() => {
  const meta = props.link.metadata
  if (meta && Array.isArray(meta.labels)) {
    return meta.labels as LinkLabel[]
  }
  return []
})

function getLabelStyle(color: string): { backgroundColor: string; borderColor: string; color: string } {
  return {
    backgroundColor: `#${color}22`,
    borderColor: `#${color}55`,
    color: `#${color}`,
  }
}
</script>

<template>
  <article class="smart-link-item">
    <component
      :is="statusIcon"
      :class="statusClass"
      :size="15"
      aria-hidden="true"
    />

    <div class="smart-link-body">
      <a
        class="smart-link-title"
        :href="link.url"
        target="_blank"
        rel="noopener noreferrer"
        @click.stop
      >
        {{ link.title || link.url }}
      </a>
      <div
        v-if="labels.length > 0"
        class="smart-link-labels"
      >
        <span
          v-for="label in labels"
          :key="label.name"
          class="smart-link-label"
          :style="getLabelStyle(label.color)"
        >
          {{ label.name }}
        </span>
      </div>
    </div>

    <button
      type="button"
      class="smart-link-dismiss"
      :aria-label="`Dismiss ${link.title || link.url}`"
      @click.stop="emit('dismiss', link.id)"
    >
      <X
        :size="12"
        aria-hidden="true"
      />
    </button>
  </article>
</template>

<style scoped>
.smart-link-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 8px;
}

.smart-link-icon {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--muted);
}

.smart-link-icon--open {
  color: #22c55e;
}

.smart-link-icon--merged {
  color: #a855f7;
}

.smart-link-icon--closed {
  color: var(--muted);
}

.smart-link-icon--draft {
  color: #f59e0b;
}

.smart-link-body {
  flex: 1;
  min-width: 0;
}

.smart-link-title {
  display: block;
  margin: 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--text);
  text-decoration: none;
  word-wrap: break-word;
}

.smart-link-title:hover {
  text-decoration: underline;
}

.smart-link-labels {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
  margin-top: 4px;
}

.smart-link-label {
  display: inline-flex;
  align-items: center;
  min-height: 18px;
  padding: 0 6px;
  border: 1px solid transparent;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
}

.smart-link-dismiss {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  margin-top: 1px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  opacity: 0;
}

.smart-link-item:hover .smart-link-dismiss {
  opacity: 1;
}

.smart-link-dismiss:hover,
.smart-link-dismiss:focus-visible {
  background: rgba(255, 255, 255, 0.08);
  color: var(--text);
  opacity: 1;
  outline: none;
}
</style>
