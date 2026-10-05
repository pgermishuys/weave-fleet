<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef, useTemplateRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Bell, ChevronRight, Laptop, Monitor, Plus, Search } from "lucide-vue-next";
import InboxAskRow from "@/components/phone/InboxAskRow.vue";
import InboxSessionRow from "@/components/phone/InboxSessionRow.vue";
import PhoneTabBar, { type PhoneTab } from "@/components/phone/PhoneTabBar.vue";
import PullIndicator from "@/components/phone/PullIndicator.vue";
import SwipeRow from "@/components/phone/SwipeRow.vue";
import { useInbox } from "@/composables/phone/use-inbox";
import { useLargeTitle } from "@/composables/phone/use-large-title";
import { phoneLook } from "@/composables/phone/use-phone-env";
import { usePhoneNav } from "@/composables/phone/use-phone-nav";
import { showToast } from "@/composables/phone/use-phone-toast";
import { usePullToRefresh } from "@/composables/phone/use-pull-to-refresh";
import { usePushSubscription } from "@/composables/phone/use-push-subscription";
import { readCredentialsSync } from "@/lib/device-credentials";
import { rememberPhoneMachine } from "@/lib/machines";
import { haptic } from "@/lib/phone/haptics";
import { holdKeyboard } from "@/lib/phone/keyboard";
import { buildInbox, machinesLine, type InboxItem } from "@/lib/phone/inbox";
import { ago, clock } from "@/lib/phone/time";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";

/**
 * The phone's home, three tabs under one tab bar. Needs you: what waits on you on every machine (answered from the
 * list), then what's working and what finished. Sessions: everything from the last month, searchable. Machines:
 * which machines the phone reaches, and notifications. Each tab has an iOS large title that folds into the bar as
 * it scrolls; Needs you pulls to refresh; a session row swipes left to archive (with Undo).
 */
const props = defineProps<{ tab: PhoneTab }>();

const router = useRouter();
const nav = usePhoneNav();
const { inbox, machines, loading, now, answerPermission, answerQuestion, setArchived, refreshAll, targetFor } = useInbox();

// Browsers rotate and drop push subscriptions: check on open and whenever the app comes back on screen. A dropped one
// is quietly made again; permission taken away gets a banner.
const push = usePushSubscription();
function checkPush(): void {
  if (document.visibilityState === "visible") void push.refresh();
}
onMounted(() => {
  void push.refresh();
  document.addEventListener("visibilitychange", checkPush);
});
onUnmounted(() => document.removeEventListener("visibilitychange", checkPush));

const ios = computed(() => phoneLook.value === "ios");
const unreachable = computed(() => machines.value.filter((m) => m.status === "unreachable" && !m.problem));
const status = computed(() => machinesLine(machines.value));
// Rows (and placeholders) until the first word from the machines, so nothing jumps in.
const firstLoad = computed(() => loading.value || (machines.value.length > 0 && machines.value.every((m) => m.status === "connecting")));

// Swiped away: hidden at once, archived on its machine; Undo brings it back.
const archived = shallowRef<ReadonlySet<string>>(new Set());
const shown = (items: readonly InboxItem[]): InboxItem[] => items.filter((item) => !archived.value.has(item.key));
const working = computed(() => shown(inbox.value.working));
const finished = computed(() => shown(inbox.value.finished));

/** Every session from the last 30 days, newest first, for the Sessions tab. */
const query = shallowRef("");
const allSessions = computed(() => {
  const built = buildInbox(machines.value, now.value, 30 * 24 * 3_600_000);
  const words = query.value.trim().toLowerCase();
  return shown([...built.needsYou, ...built.working, ...built.finished])
    .filter((item) => !words || item.title.toLowerCase().includes(words) || item.machineName.toLowerCase().includes(words))
    .sort((a, b) => b.updatedAt - a.updatedAt);
});
const today = computed(() => allSessions.value.filter((item) => now.value - item.updatedAt < 24 * 3_600_000));
const earlier = computed(() => allSessions.value.filter((item) => now.value - item.updatedAt >= 24 * 3_600_000));

function setKey(key: string, on: boolean): void {
  const next = new Set(archived.value);
  if (on) next.add(key);
  else next.delete(key);
  archived.value = next;
}

async function archive(item: InboxItem): Promise<void> {
  haptic("success");
  setKey(item.key, true);
  showToast("Archived", {
    label: "Undo",
    run: () => {
      setKey(item.key, false);
      void setArchived(item.machineId, item.sessionId, false);
    },
  });
  const outcome = await setArchived(item.machineId, item.sessionId, true);
  if (!outcome.ok && !outcome.gone) {
    setKey(item.key, false);
    showToast(outcome.error ?? "Couldn't archive it.");
  }
}

function select(next: PhoneTab): void {
  if (next === props.tab) {
    scrollerFor(next)?.scrollTo({ top: 0, behavior: "smooth" });
    return;
  }
  void router.navigate({ to: "/phone", search: next === "needs-you" ? {} : { tab: next }, replace: true });
}

async function open(item: InboxItem): Promise<void> {
  const home = machines.value.find((m) => m.isHome);
  if (item.machineId === home?.id) {
    await nav.openSession(item.machineId, item.sessionId);
    return;
  }

  // Another machine: the page reloads to work there, with the phone's own key for it.
  const target = targetFor(item.machineId);
  if (!target?.token) return;
  rememberPhoneMachine({ id: item.machineId, name: item.machineName, baseUrl: target.baseUrl, token: target.token, addedAt: new Date().toISOString() });
  window.location.assign(`/phone/s/${encodeURIComponent(item.machineId)}/${encodeURIComponent(item.sessionId)}`);
}

async function onPermission(item: InboxItem, reply: PermissionReply, message: string | undefined, done: (outcome: AnswerOutcome) => void): Promise<void> {
  if (item.ask?.kind !== "permission") return;
  done(await answerPermission(item.machineId, item.ask.ask.sessionId, item.ask.ask.id, reply, message));
}

async function onQuestion(item: InboxItem, answers: string[][], done: (outcome: AnswerOutcome) => void): Promise<void> {
  if (item.ask?.kind !== "question") return;
  done(await answerQuestion(item.machineId, item.sessionId, item.ask.requestId, answers));
}

function newSession(): void {
  // Inside the tap, so iOS brings the keyboard up with the sheet.
  holdKeyboard();
  void router.navigate({ to: "/phone/new" });
}

function notifications(): void {
  void router.navigate({ to: "/phone/setup" });
}

const homeName = computed(() => readCredentialsSync()?.homeMachineName ?? machines.value.find((m) => m.isHome)?.name ?? "This machine");

// One scroller, large title and bar per tab, so each keeps its place.
const needsScroller = useTemplateRef<HTMLElement>("needsScroller");
const needsInner = useTemplateRef<HTMLElement>("needsInner");
const needsTitle = useTemplateRef<HTMLElement>("needsTitle");
const sessionsScroller = useTemplateRef<HTMLElement>("sessionsScroller");
const sessionsTitle = useTemplateRef<HTMLElement>("sessionsTitle");
const machinesScroller = useTemplateRef<HTMLElement>("machinesScroller");
const machinesTitle = useTemplateRef<HTMLElement>("machinesTitle");
const needsBar = useLargeTitle(needsScroller, needsTitle);
const sessionsBar = useLargeTitle(sessionsScroller, sessionsTitle);
const machinesBar = useLargeTitle(machinesScroller, machinesTitle);

function scrollerFor(tab: PhoneTab): HTMLElement | null {
  return tab === "needs-you" ? needsScroller.value : tab === "sessions" ? sessionsScroller.value : machinesScroller.value;
}

const ptr = useTemplateRef<InstanceType<typeof PullIndicator>>("ptr");
const pull = usePullToRefresh({
  scroller: needsScroller,
  inner: needsInner,
  indicator: () => ptr.value?.indicator ?? null,
  stretch: needsBar.stretch,
  onRefresh: async () => {
    await Promise.all([refreshAll(), new Promise((resolve) => setTimeout(resolve, 600))]);
  },
});

// Android: the New session button shrinks to its icon while scrolling down.
const fabSmall = shallowRef(false);
let lastY = 0;
function onScroll(event: Event): void {
  const y = (event.target as HTMLElement).scrollTop;
  fabSmall.value = y > lastY && y > 40;
  lastY = y;
}
</script>

<template>
  <div
    class="inbox"
    data-testid="phone-inbox"
  >
    <!-- Needs you -->
    <div
      v-show="tab === 'needs-you'"
      class="inbox__tab"
    >
      <header
        class="ph-navbar"
        :class="{ 'ph-navbar--scrolled': needsBar.scrolled.value }"
      >
        <div class="ph-navbar__title">
          <span>Needs you</span>
        </div>
        <div class="ph-navbar__spacer" />
        <div class="ph-navbtn-group ph-glass">
          <button
            v-if="ios"
            type="button"
            class="ph-navbtn"
            aria-label="New session"
            data-testid="phone-new-session-button"
            @click="newSession"
          >
            <Plus
              :size="24"
              aria-hidden="true"
            />
          </button>
          <button
            type="button"
            class="ph-navbtn"
            aria-label="Notifications"
            data-testid="phone-notifications-button"
            @click="notifications"
          >
            <Bell
              :size="24"
              aria-hidden="true"
            />
          </button>
        </div>
      </header>
      <PullIndicator
        ref="ptr"
        :spokes="pull.spokes.value"
        :refreshing="pull.refreshing.value"
      />
      <div
        ref="needsScroller"
        class="ph-scroller"
        @scroll.passive="onScroll"
      >
        <div
          ref="needsInner"
          class="ph-scroller__inner"
        >
          <div
            ref="needsTitle"
            class="ph-large-title"
          >
            <h1>Needs you</h1>
            <p data-testid="inbox-machines-line">
              <span
                class="ph-dot ph-dot--sm"
                :class="{ 'ph-dot--live': status.tone === 'online', 'ph-dot--waiting': status.tone === 'partial', 'ph-dot--error': status.tone === 'offline' }"
                aria-hidden="true"
              />{{ status.text }}
            </p>
          </div>

          <template v-if="firstLoad">
            <div
              v-for="n in 2"
              :key="`ask-${n}`"
              class="ph-ask inbox__sk-card"
              role="presentation"
            >
              <span
                class="ph-sk"
                style="width: 38%"
              />
              <div class="inbox__sk-line">
                <span
                  class="ph-sk"
                  style="width: 72%; height: 1.05em"
                />
              </div>
              <span
                class="ph-sk"
                style="width: 30%"
              />
              <div class="inbox__sk-line">
                <span class="ph-sk inbox__sk-code" />
              </div>
              <div class="ph-btns">
                <span class="ph-sk inbox__sk-btn" />
                <span class="ph-sk inbox__sk-btn" />
              </div>
            </div>
            <div class="ph-section-h">
              <span
                class="ph-sk"
                style="width: 90px; height: 1em"
              />
            </div>
            <div class="ph-group">
              <div
                v-for="n in 2"
                :key="`row-${n}`"
                class="ph-row"
              >
                <span class="ph-dot inbox__sk-dot" />
                <div class="ph-row__main">
                  <span
                    class="ph-sk"
                    style="width: 70%"
                  />
                  <div class="inbox__sk-sub">
                    <span
                      class="ph-sk"
                      style="width: 45%; height: 0.8em"
                    />
                  </div>
                </div>
              </div>
            </div>
            <span
              role="status"
              class="sr-only"
            >Loading</span>
          </template>

          <div
            v-else
            class="ph-fade-in"
          >
            <button
              v-if="push.permissionRevoked.value"
              type="button"
              class="ph-banner"
              data-testid="push-revoked"
              @click="notifications"
            >
              Notifications are off for this phone. Turn them back on ›
            </button>
            <p
              v-for="machine in unreachable"
              :key="machine.id"
              class="ph-banner ph-banner--muted"
              data-testid="inbox-unreachable"
            >
              {{ machine.name }} unreachable<template v-if="machine.lastHeardAt">
                since {{ clock(machine.lastHeardAt) }}
              </template>. Showing what it said last.
            </p>

            <InboxAskRow
              v-for="item in inbox.needsYou"
              :key="item.key"
              :item="item"
              :now="now"
              @open="open"
              @permission="onPermission"
              @question="onQuestion"
            />
            <p
              v-if="inbox.needsYou.length === 0"
              class="inbox__quiet"
            >
              Nothing needs you right now.
            </p>

            <template v-if="working.length">
              <h2 class="ph-section-h">
                Working <small>{{ working.length }}</small>
              </h2>
              <div class="ph-group">
                <SwipeRow
                  v-for="item in working"
                  :key="item.key"
                  @archive="archive(item)"
                >
                  <InboxSessionRow
                    :item="item"
                    :now="now"
                    @open="open"
                  />
                </SwipeRow>
              </div>
            </template>

            <template v-if="finished.length">
              <h2 class="ph-section-h inbox__section-gap">
                Finished <small>{{ finished.length }}</small>
              </h2>
              <div class="ph-group">
                <SwipeRow
                  v-for="item in finished"
                  :key="item.key"
                  @archive="archive(item)"
                >
                  <InboxSessionRow
                    :item="item"
                    :now="now"
                    @open="open"
                  />
                </SwipeRow>
              </div>
            </template>
          </div>
        </div>
      </div>
    </div>

    <!-- Sessions -->
    <div
      v-show="tab === 'sessions'"
      class="inbox__tab"
    >
      <header
        class="ph-navbar"
        :class="{ 'ph-navbar--scrolled': sessionsBar.scrolled.value }"
      >
        <div class="ph-navbar__title">
          <span>Sessions</span>
        </div>
        <div class="ph-navbar__spacer" />
        <button
          v-if="ios"
          type="button"
          class="ph-navbtn ph-glass"
          aria-label="New session"
          data-testid="phone-sessions-new"
          @click="newSession"
        >
          <Plus
            :size="24"
            aria-hidden="true"
          />
        </button>
      </header>
      <div
        ref="sessionsScroller"
        class="ph-scroller"
        @scroll.passive="onScroll"
      >
        <div class="ph-scroller__inner">
          <div
            ref="sessionsTitle"
            class="ph-large-title"
          >
            <h1>Sessions</h1>
          </div>
          <div class="inbox__search">
            <label class="ph-search">
              <Search
                :size="18"
                aria-hidden="true"
              />
              <input
                v-model="query"
                type="search"
                placeholder="Search sessions"
                aria-label="Search sessions"
                enterkeyhint="search"
                data-testid="phone-sessions-search"
              >
            </label>
          </div>
          <template v-if="today.length">
            <div class="ph-group-h">
              Today
            </div>
            <div class="ph-group">
              <SwipeRow
                v-for="item in today"
                :key="item.key"
                @archive="archive(item)"
              >
                <InboxSessionRow
                  :item="item"
                  :now="now"
                  @open="open"
                />
              </SwipeRow>
            </div>
          </template>
          <template v-if="earlier.length">
            <div class="ph-group-h">
              Earlier
            </div>
            <div class="ph-group">
              <SwipeRow
                v-for="item in earlier"
                :key="item.key"
                @archive="archive(item)"
              >
                <InboxSessionRow
                  :item="item"
                  :now="now"
                  @open="open"
                />
              </SwipeRow>
            </div>
          </template>
          <p
            v-if="!firstLoad && allSessions.length === 0"
            class="inbox__quiet"
          >
            {{ query ? "No sessions match." : "No sessions in the last month." }}
          </p>
          <div class="ph-group inbox__section-gap">
            <a
              class="ph-row ph-row--accent"
              href="/?view=full"
            >
              <span class="ph-row__main"><span class="ph-row__title">Open the full Fleet</span></span>
              <ChevronRight
                class="ph-row__chev"
                :size="16"
                :stroke-width="3"
                aria-hidden="true"
              />
            </a>
          </div>
          <p class="ph-group-f">
            Swipe a session left to archive it.
          </p>
        </div>
      </div>
    </div>

    <!-- Machines -->
    <div
      v-show="tab === 'machines'"
      class="inbox__tab"
    >
      <header
        class="ph-navbar"
        :class="{ 'ph-navbar--scrolled': machinesBar.scrolled.value }"
      >
        <div class="ph-navbar__title">
          <span>Machines</span>
        </div>
      </header>
      <div
        ref="machinesScroller"
        class="ph-scroller"
      >
        <div class="ph-scroller__inner">
          <div
            ref="machinesTitle"
            class="ph-large-title"
          >
            <h1>Machines</h1>
          </div>
          <div class="ph-group">
            <div
              v-for="machine in machines"
              :key="machine.id"
              class="ph-row ph-row--static"
              style="--ph-sep-left: 62px"
              data-testid="phone-machine"
            >
              <span
                class="ph-row__icon"
                :class="machine.isHome ? 'inbox__icon--home' : 'inbox__icon--other'"
                aria-hidden="true"
              >
                <Monitor :size="20" />
              </span>
              <span class="ph-row__main">
                <span class="ph-row__title">{{ machine.name }}</span>
                <span class="ph-row__sub">
                  <template v-if="machine.problem">{{ machine.problem }}</template>
                  <template v-else-if="machine.status === 'unreachable'">Unreachable · last heard {{ ago(machine.lastHeardAt, now) || "never" }}</template>
                  <template v-else-if="machine.status === 'connecting'">Connecting…</template>
                  <template v-else>{{ machine.isHome ? "Home · sends notifications" : machine.status === "polling" ? "Checking every 15 s" : "Live" }} · {{ machine.sessions.length }} session{{ machine.sessions.length === 1 ? "" : "s" }}</template>
                </span>
              </span>
              <span
                class="ph-dot ph-dot--sm"
                :class="machine.status === 'live' || machine.status === 'polling' ? 'ph-dot--live' : machine.status === 'unreachable' ? 'ph-dot--error' : ''"
                aria-hidden="true"
              />
            </div>
          </div>
          <p class="ph-group-f">
            {{ homeName }} sends this phone's notifications for every machine here. Add machines on a computer, in
            Settings › Machines.
          </p>
          <div class="ph-group inbox__section-gap">
            <button
              type="button"
              class="ph-row"
              style="--ph-sep-left: 62px"
              data-testid="phone-machines-notifications"
              @click="notifications"
            >
              <span
                class="ph-row__icon inbox__icon--bell"
                aria-hidden="true"
              ><Bell :size="20" /></span>
              <span class="ph-row__main"><span class="ph-row__title">Notifications</span></span>
              <span class="ph-row__value">{{ push.subscribed.value ? "On" : "Off" }}</span>
              <ChevronRight
                class="ph-row__chev"
                :size="16"
                :stroke-width="3"
                aria-hidden="true"
              />
            </button>
            <a
              class="ph-row"
              href="/?view=full"
              style="--ph-sep-left: 62px"
            >
              <span
                class="ph-row__icon inbox__icon--plain"
                aria-hidden="true"
              ><Laptop :size="20" /></span>
              <span class="ph-row__main"><span class="ph-row__title">Open the full Fleet</span></span>
              <ChevronRight
                class="ph-row__chev"
                :size="16"
                :stroke-width="3"
                aria-hidden="true"
              />
            </a>
          </div>
        </div>
      </div>
    </div>

    <button
      v-if="!ios && tab !== 'machines'"
      type="button"
      class="ph-fab"
      :class="{ 'ph-fab--small': fabSmall }"
      aria-label="New session"
      data-testid="phone-new-session-button"
      @click="newSession"
    >
      <Plus
        :size="24"
        aria-hidden="true"
      />
      <span>New session</span>
    </button>
    <PhoneTabBar
      :tab="tab"
      :needs-you="inbox.needsYou.length"
      @select="select"
    />
  </div>
</template>

<style scoped>
.inbox,
.inbox__tab {
  position: absolute;
  inset: 0;
}

.inbox__quiet {
  margin: 6px 20px 8px;
  font-size: var(--ph-t-sub);
  color: var(--muted);
}

.inbox__section-gap {
  margin-top: 22px;
}

.inbox__search {
  padding: 0 16px 14px;
}

.inbox__sk-card {
  box-shadow: none;
}

.inbox__sk-line {
  margin: 12px 0 8px;
}

.inbox__sk-code {
  width: 100%;
  height: 38px;
  border-radius: 10px;
}

.inbox__sk-btn {
  height: 46px;
  border-radius: 23px;
}

.inbox__sk-dot {
  background: var(--ph-fill-strong);
}

.inbox__sk-sub {
  margin-top: 7px;
}

.ph-row__title,
.ph-row__sub {
  display: block;
}

.inbox__machine {
  cursor: default;
}

.inbox__icon--home {
  background: var(--accent);
}

.inbox__icon--other {
  background: var(--queued);
}

.inbox__icon--bell {
  background: var(--error);
}

.inbox__icon--plain {
  background: var(--muted);
}
</style>
