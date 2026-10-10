<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { ChevronRight, ExternalLink } from "lucide-vue-next";
import { useReleaseNotes } from "@/composables/use-release-notes";
import { useUpdateStatus } from "@/composables/use-update-status";
import { useDesktopUpdates } from "@/composables/use-desktop-updates";
import { useWhatsNewRequest } from "@/composables/use-whats-new";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";
import { sanitizeHtml } from "@/lib/sanitize-html";
import { compareVersions, parseReleaseNotes, releaseNotesMarkdown, whatsNewRange, type ReleaseNote } from "@/lib/release-notes";

/** The public list both the app and the CLI update from. */
const ALL_RELEASES_URL = "https://github.com/pgermishuys/fleet-releases/releases";

const { releases, fetchedAt, error, isLoading, load } = useReleaseNotes();
const { updateStatus } = useUpdateStatus();
const { state: appUpdate } = useDesktopUpdates();
const { request, clear } = useWhatsNewRequest();
const markdown = sharedMarkdownRenderer();

const installed = computed(() => updateStatus.value?.currentVersion ?? appUpdate.value?.currentVersion ?? null);
const range = computed(() => whatsNewRange(releases.value, installed.value));

/** The version an update has downloaded and installs on restart, from the app or the server. */
const readyVersion = computed(() => {
  if (appUpdate.value?.status === "ready") return appUpdate.value.version ?? null;
  if (updateStatus.value?.status === "staged") return updateStatus.value.latestVersion;
  return null;
});

function badge(release: ReleaseNote): { label: string; tone: "accent" | "muted" } | null {
  if (release.version === readyVersion.value) return { label: "Ready to install", tone: "accent" };
  if (installed.value && release.version === installed.value) return { label: "Installed", tone: "muted" };
  if (installed.value && compareVersions(release.version, installed.value) > 0) return { label: "Available", tone: "accent" };
  return null;
}

const rendered = computed(() => {
  const notes = new Map<string, { html: string; internal: number }>();
  for (const release of [...range.value.open, ...range.value.folded]) {
    const parsed = parseReleaseNotes(release.body);
    notes.set(release.version, { html: sanitizeHtml(markdown.render(releaseNotesMarkdown(parsed))), internal: parsed.internal });
  }
  return notes;
});

function internalLine(count: number): string {
  return count === 1 ? "And 1 behind-the-scenes change." : `And ${count} behind-the-scenes changes.`;
}

function formatDate(iso: string | null): string {
  if (!iso) return "";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

const savedOn = computed(() => formatDate(fetchedAt.value));

// The notes' links (pull requests, docs) open in the browser; Fleet's own window stays put.
function openLink(event: MouseEvent): void {
  const link = (event.target as HTMLElement | null)?.closest("a");
  const href = link?.getAttribute("href");
  if (!href || !/^https?:/i.test(href)) return;
  event.preventDefault();
  window.open(href, "_blank", "noopener,noreferrer");
}

// ── Arriving from a "What's new" link: scroll to that version and mark it ──
const card = ref<HTMLElement | null>(null);
const flashed = ref<string | null>(null);

async function goToRequest(): Promise<void> {
  const target = request.value;
  if (!target || isLoading.value) return;
  clear();
  await nextTick();
  const row = target.version
    ? Array.from(card.value?.querySelectorAll<HTMLDetailsElement>("details[data-version]") ?? []).find((el) => el.dataset.version === target.version)
    : undefined;
  if (target.since && target.version) {
    // From "What's new since …": every version the update brought is open, not only the newest.
    for (const el of card.value?.querySelectorAll<HTMLDetailsElement>("details[data-version]") ?? []) {
      const version = el.dataset.version ?? "";
      if (compareVersions(version, target.since) > 0 && compareVersions(version, target.version) <= 0) el.open = true;
    }
  }
  if (row) {
    row.open = true;
    flashed.value = null;
    await nextTick();
    flashed.value = target.version;
  }
  (row ?? card.value)?.scrollIntoView({ block: "start", behavior: "smooth" });
}

watch([request, isLoading], () => void goToRequest());

onMounted(async () => {
  await load();
  await goToRequest();
});
</script>

<template>
  <section
    ref="card"
    class="whats-new rounded-card border border-border bg-card-bg p-6 shadow-sm"
    data-testid="whats-new"
  >
    <div class="flex items-start justify-between gap-4">
      <div class="flex flex-col gap-1">
        <h2 class="text-lg font-semibold text-text">
          What's new
        </h2>
        <p class="text-sm text-muted">
          What changed in each version of Fleet.
        </p>
      </div>
      <a
        :href="ALL_RELEASES_URL"
        target="_blank"
        rel="noopener noreferrer"
        class="inline-flex shrink-0 items-center gap-1 text-xs text-muted underline underline-offset-2 transition-colors hover:text-text"
      >
        All releases on GitHub
        <ExternalLink
          :size="12"
          aria-hidden="true"
        />
      </a>
    </div>

    <p
      v-if="isLoading && releases.length === 0"
      class="mt-4 text-sm text-muted"
    >
      Loading release notes…
    </p>
    <p
      v-else-if="releases.length === 0"
      class="mt-4 text-sm text-muted"
      data-testid="whats-new-empty"
    >
      {{ error ? `Fleet couldn't get the release notes (${error}).` : "There are no release notes yet." }}
      They're on <a
        :href="ALL_RELEASES_URL"
        target="_blank"
        rel="noopener noreferrer"
        class="underline underline-offset-2 hover:text-text"
      >GitHub</a>.
    </p>

    <div
      v-else
      class="mt-3"
    >
      <p
        v-if="error"
        class="mb-2 text-xs text-muted"
        data-testid="whats-new-stale"
      >
        Couldn't check GitHub for newer notes ({{ error }}). These are from {{ savedOn }}.
      </p>
      <details
        v-for="release in [...range.open, ...range.folded]"
        :key="release.version"
        class="whats-new__version"
        :class="{ 'whats-new__version--flash': flashed === release.version }"
        :data-version="release.version"
        :open="range.open.includes(release)"
        data-testid="whats-new-version"
      >
        <summary class="whats-new__summary">
          <ChevronRight
            :size="14"
            class="whats-new__chevron text-muted"
            aria-hidden="true"
          />
          <span class="text-sm font-semibold text-text">Fleet {{ release.version }}</span>
          <span
            v-if="badge(release)"
            class="whats-new__badge"
            :class="{ 'whats-new__badge--accent': badge(release)?.tone === 'accent' }"
          >{{ badge(release)?.label }}</span>
          <span class="ml-auto font-mono text-xs text-muted">{{ formatDate(release.publishedAt) }}</span>
        </summary>
        <!-- eslint-disable vue/no-v-html -- release notes rendered by Fleet's Markdown renderer and sanitized -->
        <div
          class="whats-new__notes md-content"
          @click="openLink"
          v-html="rendered.get(release.version)?.html"
        />
        <!-- eslint-enable vue/no-v-html -->
        <p
          v-if="rendered.get(release.version)?.internal"
          class="whats-new__internal"
          data-testid="whats-new-internal"
        >
          {{ internalLine(rendered.get(release.version)?.internal ?? 0) }}
          <a
            :href="release.url"
            target="_blank"
            rel="noopener noreferrer"
          >All the notes on GitHub</a>
        </p>
      </details>
    </div>
  </section>
</template>

<style scoped>
.whats-new {
  scroll-margin-top: 16px;
}

.whats-new__version {
  border-top: 1px solid var(--border);
  border-radius: 8px;
  scroll-margin-top: 16px;
}

.whats-new__version:first-of-type {
  border-top: 0;
}

.whats-new__summary {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 0;
  cursor: pointer;
  list-style: none;
}

.whats-new__summary::-webkit-details-marker {
  display: none;
}

.whats-new__chevron {
  flex: none;
  transition: transform 150ms ease;
}

.whats-new__version[open] .whats-new__chevron {
  transform: rotate(90deg);
}

.whats-new__badge {
  display: inline-flex;
  align-items: center;
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 1px 8px;
  font-size: 10px;
  font-weight: 500;
  color: var(--muted);
}

.whats-new__badge--accent {
  border-color: color-mix(in srgb, var(--accent) 30%, transparent);
  background: color-mix(in srgb, var(--accent) 10%, transparent);
  color: var(--accent);
}

.whats-new__notes {
  padding: 0 0 16px 24px;
  font-size: 13.5px;
}

.whats-new__notes :deep(h4) {
  margin: 14px 0 4px;
  font-size: 11px;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--muted);
}

.whats-new__notes :deep(h4:first-child) {
  margin-top: 2px;
}

/* The pull request number after each change: there to follow, not to read. */
.whats-new__notes :deep(li > a[href*="/pull/"]:last-child) {
  margin-left: 2px;
  font-family: var(--font-mono-stack, ui-monospace, monospace);
  font-size: 11.5px;
  color: var(--muted);
  text-decoration: none;
}

.whats-new__notes :deep(li > a[href*="/pull/"]:last-child:hover) {
  color: var(--accent);
}

/* The part of Fleet a change is about ("Claude Code"): a quiet label before it. */
.whats-new__notes :deep(li > em:first-child) {
  margin-right: 4px;
  font-size: 12px;
  font-style: normal;
  color: var(--muted);
}

.whats-new__internal {
  margin: -8px 0 0;
  padding: 0 0 16px 24px;
  font-size: 12px;
  color: var(--muted);
}

.whats-new__internal a {
  color: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
}

.whats-new__internal a:hover {
  color: var(--text);
}

.whats-new__version--flash {
  animation: whats-new-flash 1.6s ease-out;
}

@keyframes whats-new-flash {
  0%,
  40% {
    background: color-mix(in srgb, var(--accent) 9%, transparent);
  }

  100% {
    background: transparent;
  }
}

@media (prefers-reduced-motion: reduce) {
  .whats-new__chevron {
    transition: none;
  }

  /* The part of Fleet a change is about ("Claude Code"): a quiet label before it. */
.whats-new__notes :deep(li > em:first-child) {
  margin-right: 4px;
  font-size: 12px;
  font-style: normal;
  color: var(--muted);
}

.whats-new__internal {
  margin: -8px 0 0;
  padding: 0 0 16px 24px;
  font-size: 12px;
  color: var(--muted);
}

.whats-new__internal a {
  color: inherit;
  text-decoration: underline;
  text-underline-offset: 2px;
}

.whats-new__internal a:hover {
  color: var(--text);
}

.whats-new__version--flash {
    animation: none;
  }
}
</style>
