<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { CircleDot, Copy, ExternalLink, Github, LoaderCircle, Plus, RefreshCw, TriangleAlert, Zap } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import AddRepositoryDialog from "@/components/github/AddRepositoryDialog.vue";
import GitHubItemRow from "@/components/github/GitHubItemRow.vue";
import { useGitHubSessions } from "@/composables/use-github-sessions";
import { useGitHubWork } from "@/composables/use-github-work";
import { useRelativeTime } from "@/composables/use-relative-time";
import { formatRelativeTime } from "@/lib/format-utils";
import {
  itemResourceId,
  itemRoute,
  itemSessionPreset,
  needsYou,
  summaryFromLink,
  yourPullRequests,
  type GitHubItemSummary,
} from "@/lib/github-items";
import { useGitHubAuth } from "@/plugins/builtin/github/composables/use-github-auth";
import { useGitHubBookmarks } from "@/plugins/builtin/github/composables/use-github-bookmarks";
import { useSmartLinksStore } from "@/stores/smart-links";

const router = useRouter();
const smartLinks = useSmartLinksStore();
const { isConnected, isLoadingStatus, deviceState, isAwaitingAuthorization, connectWithDeviceFlow, resetDeviceFlow, copyUserCode } = useGitHubAuth();
const { bookmarks } = useGitHubBookmarks();
const { work, isLoading, error, loadedAt, load, refresh } = useGitHubWork();
const now = useRelativeTime();
const { sessionsFor, openSession, startSession } = useGitHubSessions();

const isAddOpen = shallowRef(false);

onMounted(() => {
  if (isConnected.value) void load();
});
watch(isConnected, (connected) => {
  if (connected) void load(true);
});

const needs = computed(() => (work.value ? needsYou(work.value) : []));
const listed = computed(() => new Set(needs.value.map(({ item }) => itemResourceId(item).toLowerCase())));

/** Pull requests and issues a session is on, from the sessions' own links; open ones only. */
const inFleet = computed(() => {
  const seen = new Set<string>();
  const items: GitHubItemSummary[] = [];
  for (const links of Object.values(smartLinks.headerBySession)) {
    for (const link of links) {
      const key = link.resourceId.toLowerCase();
      if (link.isTerminal || link.enrichmentStatus !== "resolved" || seen.has(key) || listed.value.has(key)) continue;
      const item = summaryFromLink(link);
      if (!item || sessionsFor(link.resourceId).length === 0) continue;
      seen.add(key);
      items.push(item);
    }
  }
  return items.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
});

const inFleetKeys = computed(() => new Set(inFleet.value.map((item) => itemResourceId(item).toLowerCase())));
const notElsewhere = (item: GitHubItemSummary) => {
  const key = itemResourceId(item).toLowerCase();
  return !listed.value.has(key) && !inFleetKeys.value.has(key);
};
const yourPulls = computed(() => (work.value ? yourPullRequests(work.value).filter(notElsewhere) : []));
const assigned = computed(() => (work.value?.assigned ?? []).filter(notElsewhere));
const isEmpty = computed(() => needs.value.length + inFleet.value.length + yourPulls.value.length + assigned.value.length === 0);
const scope = computed(() => (bookmarks.value.length === 0
  ? "Everything you're involved in"
  : `${bookmarks.value.length} ${bookmarks.value.length === 1 ? "repository" : "repositories"}`));

function sessions(item: GitHubItemSummary): SessionListItem[] {
  return sessionsFor(itemResourceId(item));
}

function open(item: GitHubItemSummary): void {
  void router.navigate({ to: itemRoute(item) as string });
}

function start(item: GitHubItemSummary): void {
  void startSession(itemSessionPreset(item));
}

function openSettings(): void {
  void router.navigate({ to: "/settings/plugins/$pluginId", params: { pluginId: "github" } });
}

const expiresIn = computed(() => (deviceState.value.status === "awaiting-auth"
  ? Math.max(0, Math.round((deviceState.value.expiresAt - Date.now()) / 60_000))
  : 0));
</script>

<template>
  <div class="gh-home">
    <!-- Not connected: what connecting gets you, and the connection itself. -->
    <section
      v-if="!isLoadingStatus && !isConnected"
      class="gh-connect"
      data-testid="github-connect"
    >
      <Github
        :size="34"
        class="gh-connect__mark"
        aria-hidden="true"
      />
      <h1 class="gh-connect__title">
        Connect GitHub
      </h1>
      <p class="gh-connect__lede">
        See which pull requests need you, watch each session's checks and reviews, and start a session from any
        issue.
      </p>

      <div
        v-if="deviceState.status === 'awaiting-auth' && isAwaitingAuthorization"
        class="gh-connect__code"
      >
        <span class="gh-connect__hint">Enter this code at
          <a
            :href="deviceState.verificationUri"
            target="_blank"
            rel="noreferrer noopener"
          >{{ deviceState.verificationUri.replace(/^https?:\/\//, "") }}</a></span>
        <span class="gh-connect__digits">{{ deviceState.userCode }}</span>
        <span class="gh-connect__actions">
          <button
            type="button"
            class="gh-home__btn"
            @click="copyUserCode(deviceState.userCode)"
          >
            <Copy
              :size="13"
              aria-hidden="true"
            />
            Copy code
          </button>
          <a
            class="gh-home__btn"
            :href="deviceState.verificationUri"
            target="_blank"
            rel="noreferrer noopener"
          >
            <ExternalLink
              :size="13"
              aria-hidden="true"
            />
            Open GitHub
          </a>
        </span>
        <span class="gh-connect__hint">
          <LoaderCircle
            :size="13"
            class="gh-home__spin"
            aria-hidden="true"
          />
          Waiting for you to approve it{{ expiresIn ? ` (the code works for ${expiresIn} more minutes)` : "" }}…
        </span>
        <button
          type="button"
          class="gh-home__link"
          @click="resetDeviceFlow"
        >
          Cancel
        </button>
      </div>

      <template v-else>
        <p
          v-if="deviceState.status === 'expired' || deviceState.status === 'denied' || deviceState.status === 'error'"
          class="gh-connect__error"
          role="alert"
        >
          <TriangleAlert
            :size="14"
            aria-hidden="true"
          />
          {{ deviceState.status === "expired" ? "The code expired. Start again for a new one."
            : deviceState.status === "denied" ? "GitHub says the request was denied."
              : deviceState.message }}
        </p>
        <button
          type="button"
          class="gh-home__btn gh-home__btn--primary"
          :disabled="deviceState.status === 'initiating'"
          data-testid="github-connect-start"
          @click="connectWithDeviceFlow"
        >
          <LoaderCircle
            v-if="deviceState.status === 'initiating'"
            :size="14"
            class="gh-home__spin"
            aria-hidden="true"
          />
          <Github
            v-else
            :size="14"
            aria-hidden="true"
          />
          Connect with GitHub
        </button>
        <button
          type="button"
          class="gh-home__link"
          @click="openSettings"
        >
          Use a personal access token instead
        </button>
      </template>
    </section>

    <template v-else-if="isConnected">
      <header class="gh-home__head">
        <div>
          <h1 class="gh-home__title">
            Your work
          </h1>
          <p class="gh-home__sub">
            {{ scope }}<template v-if="loadedAt">
              · updated {{ formatRelativeTime(loadedAt, now) }}
            </template>
          </p>
        </div>
        <button
          type="button"
          class="gh-home__btn gh-home__btn--ghost"
          :disabled="isLoading"
          aria-label="Refresh"
          title="Refresh"
          @click="refresh"
        >
          <RefreshCw
            :size="14"
            :class="{ 'gh-home__spin': isLoading }"
            aria-hidden="true"
          />
        </button>
        <button
          type="button"
          class="gh-home__btn"
          @click="isAddOpen = true"
        >
          <Plus
            :size="14"
            aria-hidden="true"
          />
          Follow a repository
        </button>
      </header>

      <p
        v-if="error"
        class="gh-home__error"
        role="alert"
      >
        <TriangleAlert
          :size="14"
          aria-hidden="true"
        />
        {{ error }}
      </p>

      <div
        v-if="!work && isLoading"
        class="gh-home__loading"
      >
        <LoaderCircle
          :size="16"
          class="gh-home__spin"
          aria-hidden="true"
        />
        Asking GitHub what needs you…
      </div>

      <template v-else-if="work">
        <section
          v-if="needs.length"
          class="gh-home__bucket"
          data-testid="github-needs-you"
        >
          <h2 class="gh-home__bucket-title">
            <TriangleAlert
              :size="13"
              class="gh-home__bucket-icon gh-home__bucket-icon--warn"
              aria-hidden="true"
            />
            Needs you <span class="gh-home__count">{{ needs.length }}</span>
          </h2>
          <GitHubItemRow
            v-for="{ item, reason } in needs"
            :key="itemResourceId(item)"
            :item="item"
            :reason="reason"
            :sessions="sessions(item)"
            show-repo
            @open="open"
            @start="start"
            @open-session="openSession"
          />
        </section>

        <section
          v-if="inFleet.length"
          class="gh-home__bucket"
          data-testid="github-in-fleet"
        >
          <h2 class="gh-home__bucket-title">
            <Zap
              :size="13"
              class="gh-home__bucket-icon gh-home__bucket-icon--fleet"
              aria-hidden="true"
            />
            In Fleet <span class="gh-home__count">{{ inFleet.length }}</span>
          </h2>
          <GitHubItemRow
            v-for="item in inFleet"
            :key="itemResourceId(item)"
            :item="item"
            :sessions="sessions(item)"
            show-repo
            @open="open"
            @start="start"
            @open-session="openSession"
          />
        </section>

        <section
          v-if="yourPulls.length"
          class="gh-home__bucket"
        >
          <h2 class="gh-home__bucket-title">
            Your pull requests <span class="gh-home__count">{{ yourPulls.length }}</span>
          </h2>
          <GitHubItemRow
            v-for="item in yourPulls"
            :key="itemResourceId(item)"
            :item="item"
            :sessions="sessions(item)"
            show-repo
            @open="open"
            @start="start"
            @open-session="openSession"
          />
        </section>

        <section
          v-if="assigned.length"
          class="gh-home__bucket"
        >
          <h2 class="gh-home__bucket-title">
            <CircleDot
              :size="13"
              class="gh-home__bucket-icon"
              aria-hidden="true"
            />
            Assigned to you <span class="gh-home__count">{{ assigned.length }}</span>
          </h2>
          <GitHubItemRow
            v-for="item in assigned"
            :key="itemResourceId(item)"
            :item="item"
            :sessions="sessions(item)"
            show-repo
            @open="open"
            @start="start"
            @open-session="openSession"
          />
        </section>

        <div
          v-if="isEmpty"
          class="gh-home__empty"
        >
          <p class="gh-home__empty-title">
            Nothing needs you.
          </p>
          <p>
            No review requests, open pull requests or assigned issues{{ bookmarks.length ? " in the repositories you follow" : "" }}.
          </p>
        </div>
      </template>
    </template>

    <AddRepositoryDialog
      v-model:open="isAddOpen"
      @added="refresh"
    />
  </div>
</template>

<style scoped>
.gh-home {
  display: flex;
  flex-direction: column;
  gap: 22px;
  max-width: 1080px;
  min-height: 100%;
  margin: 0 auto;
  color: var(--text);
}

.gh-home__head {
  display: flex;
  align-items: flex-end;
  gap: 8px;
}

.gh-home__head > div {
  flex: 1;
  min-width: 0;
}

.gh-home__title {
  margin: 0;
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.01em;
  line-height: 1.2;
}

.gh-home__sub {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 12.5px;
}

.gh-home__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 11px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
}

.gh-home__btn:disabled {
  opacity: 0.7;
  cursor: default;
}

.gh-home__btn:focus-visible,
.gh-home__link:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.gh-home__btn--ghost {
  border-color: transparent;
  background: transparent;
  color: var(--muted);
}

.gh-home__btn--primary {
  justify-content: center;
  height: 34px;
  border-color: transparent;
  background: var(--accent);
  color: var(--primary-foreground);
}

.gh-home__link {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  cursor: pointer;
}

.gh-home__link:hover {
  color: var(--text);
  text-decoration: underline;
}

.gh-home__spin {
  animation: gh-home-spin 1s linear infinite;
}

@keyframes gh-home-spin {
  to { transform: rotate(360deg); }
}

.gh-home__error {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  color: var(--error);
  font-size: 13px;
}

.gh-home__loading {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--muted);
}

.gh-home__bucket {
  display: grid;
  gap: 2px;
}

.gh-home__bucket-title {
  display: flex;
  align-items: center;
  gap: 7px;
  margin: 0 0 4px;
  padding: 0 10px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}

.gh-home__bucket-icon {
  flex-shrink: 0;
}

.gh-home__bucket-icon--warn { color: var(--pr-blocked); }
.gh-home__bucket-icon--fleet { color: var(--accent); }

.gh-home__count {
  color: color-mix(in srgb, var(--muted) 75%, transparent);
  font-family: var(--font-mono-stack);
  font-weight: 400;
}

.gh-home__empty {
  padding: 48px 16px;
  color: var(--muted);
  text-align: center;
}

.gh-home__empty p {
  margin: 0;
}

.gh-home__empty-title {
  margin-bottom: 4px !important;
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

/* ── Connect ── */
.gh-connect {
  display: grid;
  justify-items: center;
  gap: 12px;
  width: 100%;
  max-width: 440px;
  margin: 12vh auto 0;
  text-align: center;
}

.gh-connect__mark {
  color: var(--muted);
}

.gh-connect__title {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
}

.gh-connect__lede {
  margin: 0 0 6px;
  color: var(--muted);
  line-height: 1.5;
}

.gh-connect__code {
  display: grid;
  justify-items: center;
  gap: 10px;
  width: 100%;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.gh-connect__hint {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12.5px;
}

.gh-connect__hint a {
  color: var(--text);
  font-family: var(--font-mono-stack);
}

.gh-connect__digits {
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 24px;
  font-weight: 500;
  letter-spacing: 0.2em;
}

.gh-connect__actions {
  display: flex;
  gap: 6px;
}

.gh-connect__error {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  color: var(--error);
  font-size: 12.5px;
}

@media (prefers-reduced-motion: reduce) {
  .gh-home__spin { animation: none; }
}
</style>
