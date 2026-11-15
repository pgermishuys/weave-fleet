<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { ChevronLeft, ChevronRight } from "lucide-vue-next"
import { cn } from "@/lib/utils"

/** One step of the way back: a page above this one. */
export interface PageHeaderLink {
  label: string
  /** The address, so the link can be opened in a new tab. */
  href?: string
  /** Goes there in the app (a router navigation); the link's own navigation is then skipped. */
  onSelect?: () => void
}

// The one page title: the top of every page a rail view, Settings or Repositories shows. A title at one size (the
// theme's text-2xl, from Settings), an optional line under it, and actions on the right. `trail` is the way back for a
// detail page: one link reads "‹ Automations", more read as a trail ("GitHub › owner / repo › Issues"). `bar` is for
// a page whose body scrolls under the header (Jobs, Fleet, Board): full width, its own padding and a rule under it.
// Without it the header sits in a page that already has padding. On a phone the header keeps clear of the floating
// menu button (phone-menu-lane) and its actions go on their own row under the title.
const props = withDefaults(
  defineProps<{
    title?: string
    description?: string
    trail?: readonly PageHeaderLink[]
    /** A small label above the title. */
    eyebrow?: string
    bar?: boolean
    /** 1 for a page; 2 when it heads a pane inside a page that has its own h1. */
    level?: 1 | 2
    class?: HTMLAttributes["class"]
  }>(),
  { title: undefined, description: undefined, trail: undefined, eyebrow: undefined, bar: false, level: 1, class: undefined },
)

function follow(event: MouseEvent, link: PageHeaderLink): void {
  if (!link.onSelect) return
  // A modified click opens the address in a new tab or window, as any link does.
  if (link.href && (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0)) return
  event.preventDefault()
  link.onSelect()
}
</script>

<template>
  <header
    data-slot="page-header"
    :class="cn('page-header phone-menu-lane', props.bar && 'page-header--bar', props.class)"
  >
    <div class="page-header__lead">
      <nav
        v-if="props.trail?.length"
        class="page-header__trail"
        :aria-label="props.trail.length === 1 ? 'Back' : 'Breadcrumbs'"
        data-slot="page-header-trail"
      >
        <template
          v-for="(link, index) in props.trail"
          :key="index"
        >
          <ChevronLeft
            v-if="props.trail.length === 1"
            class="size-3.5 shrink-0"
            aria-hidden="true"
          />
          <ChevronRight
            v-else-if="index > 0"
            class="size-3 shrink-0"
            aria-hidden="true"
          />
          <a
            v-if="link.href || link.onSelect"
            class="page-header__link"
            :href="link.href"
            @click="follow($event, link)"
          >{{ link.label }}</a>
          <span v-else>{{ link.label }}</span>
        </template>
      </nav>
      <p
        v-if="props.eyebrow"
        class="page-header__eyebrow"
      >
        {{ props.eyebrow }}
      </p>
      <div class="page-header__title-row">
        <slot name="leading" />
        <slot name="heading">
          <component
            :is="`h${props.level}`"
            class="page-header__title text-2xl font-semibold tracking-tight text-text"
            data-slot="page-header-title"
          >
            <slot name="title">
              {{ props.title }}
            </slot>
          </component>
        </slot>
        <slot name="meta" />
      </div>
      <p
        v-if="props.description || $slots.description"
        class="page-header__description text-sm text-muted"
      >
        <slot name="description">
          {{ props.description }}
        </slot>
      </p>
      <slot />
    </div>
    <div
      v-if="$slots.actions"
      class="page-header__actions"
      data-slot="page-header-actions"
    >
      <slot name="actions" />
    </div>
  </header>
</template>

<style scoped>
.page-header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px 16px;
  min-width: 0;
}

.page-header--bar {
  flex-shrink: 0;
  border-bottom: 1px solid var(--border);
  padding: 14px max(1.25rem, env(safe-area-inset-right)) 14px max(1.25rem, env(safe-area-inset-left));
}

.page-header__lead {
  display: flex;
  min-width: 0;
  flex: 1 1 16rem;
  flex-direction: column;
}

.page-header__trail {
  display: flex;
  min-width: 0;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
  margin-bottom: 6px;
  color: var(--muted);
  font-size: 0.85714rem;
  line-height: 1.4;
}

.page-header__link {
  color: inherit;
  text-decoration: none;
  transition: color var(--transition);
}

.page-header__link:hover {
  color: var(--text);
}

.page-header__eyebrow {
  margin: 0 0 6px;
  color: var(--muted);
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.page-header__title-row {
  display: flex;
  min-width: 0;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 10px;
}

/* Settings' title (text-2xl, semibold, tight, set in the template). Every page title is this one. */
.page-header__title {
  min-width: 0;
  margin: 0;
  line-height: 1.25;
  overflow-wrap: anywhere;
}

.page-header__description {
  margin: 4px 0 0;
  line-height: 1.5;
}

.page-header__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  margin-left: auto;
}

/* A phone: the actions take their own row under the title, from its left edge. All of it inside :global(), since
   `:global(.a) .b` compiles to `.a` alone. */
:global(.app--phone .page-header__actions) {
  flex-basis: 100%;
  justify-content: flex-start;
  margin-left: 0;
}
</style>
