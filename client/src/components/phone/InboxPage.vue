<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef, useTemplateRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Bell, Check, ChevronDown, ChevronRight, ExternalLink, Laptop, Plus, Search, Smartphone } from "lucide-vue-next";
import weaveLogo from "@/assets/weave_logo.png";
import InboxAskRow from "@/components/phone/InboxAskRow.vue";
import InboxSessionRow from "@/components/phone/InboxSessionRow.vue";
import PhoneTabBar, { type PhoneTab } from "@/components/phone/PhoneTabBar.vue";
import PullIndicator from "@/components/phone/PullIndicator.vue";
import SwipeRow from "@/components/phone/SwipeRow.vue";
import { useInbox } from "@/composables/phone/use-inbox";
import { useLargeTitle } from "@/composables/phone/use-large-title";
import { usePhoneNav } from "@/composables/phone/use-phone-nav";
import { showToast } from "@/composables/phone/use-phone-toast";
import { usePullToRefresh } from "@/composables/phone/use-pull-to-refresh";
import { usePushSubscription } from "@/composables/phone/use-push-subscription";
import { readCredentialsSync } from "@/lib/device-credentials";
import { rememberPhoneMachine } from "@/lib/machines";
import { haptic } from "@/lib/phone/haptics";
import { holdKeyboard } from "@/lib/phone/keyboard";
import { buildInbox, machinesStatus, type InboxItem } from "@/lib/phone/inbox";
import { ago, clock } from "@/lib/phone/time";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";

/**
 * The phone's home, in Fleet's look: the chrome bar (logo, Notifications, New session), a panel with three pages,
 * and the rail laid along the bottom as tabs. Needs you: what waits on you on every machine (answered from the list),
 * then what's working and what finished. Sessions: everything from the last month by machine, filterable. Machines:
 * which machines the phone reaches, and this phone's own settings. Each page's head scrolls away into the bar; Needs
 * you pulls to refresh; a session row swipes left to archive (with Undo).
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

const TITLES: Record<PhoneTab, string> = { "needs-you": "Needs you", sessions: "Sessions", machines: "Machines" };
const unreachable = computed(() => machines.value.filter((m) => m.status === "unreachable" && !m.problem));
const status = computed(() => machinesStatus(machines.value));
// Rows (and placeholders) until the first word from the machines, so nothing jumps in.
const firstLoad = computed(() => loading.value || (machines.value.length > 0 && machines.value.every((m) => m.status === "connecting")));

// Swiped away: hidden at once, archived on its machine; Undo brings it back.
const archived = shallowRef<ReadonlySet<string>>(new Set());
const shown = (items: readonly InboxItem[]): InboxItem[] => items.filter((item) => !archived.value.has(item.key));
const working = computed(() => shown(inbox.value.working));
const finishedAll = computed(() => shown(inbox.value.finished));
/** The newest few; the rest are a tap away under Sessions. */
const finished = computed(() => finishedAll.value.slice(0, 3));

/** Every session from the last 30 days, by machine: what needs you first, then newest first. */
const query = shallowRef("");
const recent = computed(() => {
  const built = buildInbox(machines.value, now.value, 30 * 24 * 3_600_000);
  return shown([...built.needsYou, ...built.working, ...built.finished]);
});
const allSessions = computed(() => {
  const words = query.value.trim().toLowerCase();
  return recent.value.filter((item) => !words || [item.title, item.machineName, item.folder ?? "", item.branch ?? ""].some((text) => text.toLowerCase().includes(words)));
});
const byMachine = computed(() => machines.value
  .map((machine) => ({
    machine,
    items: allSessions.value
      .filter((item) => item.machineId === machine.id)
      .sort((a, b) => Number(b.status === "waiting_input") - Number(a.status === "waiting_input") || b.updatedAt - a.updatedAt),
  }))
  .filter((group) => group.items.length > 0 || !query.value.trim()));
const sessionsLine = computed(() => {
  const total = recent.value.length;
  return `${total} session${total === 1 ? "" : "s"} on ${machines.value.length} machine${machines.value.length === 1 ? "" : "s"}`;
});

// A machine's sessions fold under its heading, as the desktop's machine headers do; remembered on this phone.
const FOLDS_KEY = "weave:phone-machine-folds";
function readFolds(): Set<string> {
  try {
    const stored = JSON.parse(localStorage.getItem(FOLDS_KEY) ?? "[]") as unknown;
    return new Set(Array.isArray(stored) ? stored.filter((id): id is string => typeof id === "string") : []);
  } catch {
    return new Set();
  }
}
const folded = shallowRef<ReadonlySet<string>>(readFolds());
function toggleFold(id: string): void {
  const next = new Set(folded.value);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  folded.value = next;
  haptic("light");
  try {
    localStorage.setItem(FOLDS_KEY, JSON.stringify([...next]));
  } catch {
    // no storage: folds last this visit
  }
}

function setKey(key: string, on: boolean): void {
  const next = new Set(archived.value);
  if (on) next.add(key);
  else next.delete(key);
  archived.value = next;
}

async function archive(item: InboxItem): Promise<void> {
  haptic("success");
  setKey(item.key, true);
  showToast(`Archived “${item.title}”`, {
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

/** A machine's row under Machines: its sessions, under Sessions. */
function showMachine(name: string): void {
  query.value = name;
  select("sessions");
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
const machinesLine = computed(() => {
  const online = machines.value.filter((m) => m.status === "live" || m.status === "polling").length;
  return `${online} online · this phone is paired with ${homeName.value}`;
});

function machineDetail(machine: (typeof machines.value)[number]): string {
  if (machine.problem) return machine.problem;
  if (machine.status === "unreachable") return `Unreachable · last heard ${ago(machine.lastHeardAt, now.value) || "never"}`;
  if (machine.status === "connecting") return "Connecting…";
  const count = `${machine.sessions.length} session${machine.sessions.length === 1 ? "" : "s"}`;
  if (machine.isHome) return `${count} · this phone's home`;
  return `${count} · ${machine.status === "polling" ? "checked every 15 s" : `reached through ${homeName.value}`}`;
}

// One scroller and page head per tab, so each keeps its place; the bar follows the one on screen.
const needsScroller = useTemplateRef<HTMLElement>("needsScroller");
const needsInner = useTemplateRef<HTMLElement>("needsInner");
const needsTitle = useTemplateRef<HTMLElement>("needsTitle");
const sessionsScroller = useTemplateRef<HTMLElement>("sessionsScroller");
const sessionsTitle = useTemplateRef<HTMLElement>("sessionsTitle");
const machinesScroller = useTemplateRef<HTMLElement>("machinesScroller");
const machinesTitle = useTemplateRef<HTMLElement>("machinesTitle");
const heads = {
  "needs-you": useLargeTitle(needsScroller, needsTitle),
  sessions: useLargeTitle(sessionsScroller, sessionsTitle),
  machines: useLargeTitle(machinesScroller, machinesTitle),
};
const barScrolled = computed(() => heads[props.tab].scrolled.value);

function scrollerFor(tab: PhoneTab): HTMLElement | null {
  return tab === "needs-you" ? needsScroller.value : tab === "sessions" ? sessionsScroller.value : machinesScroller.value;
}

const ptr = useTemplateRef<InstanceType<typeof PullIndicator>>("ptr");
const pull = usePullToRefresh({
  scroller: needsScroller,
  inner: needsInner,
  indicator: () => ptr.value?.indicator ?? null,
  onRefresh: async () => {
    await Promise.all([refreshAll(), new Promise((resolve) => setTimeout(resolve, 600))]);
  },
});
</script>

<template>
  <div
    class="inbox"
    data-testid="phone-inbox"
  >
    <header
      class="ph-bar"
      :class="{ 'ph-bar--scrolled': barScrolled }"
    >
      <img
        class="ph-bar__logo"
        :src="weaveLogo"
        alt="Fleet"
      >
      <span class="ph-bar__title">{{ TITLES[tab] }}</span>
      <span class="ph-bar__spacer" />
      <button
        type="button"
        class="ph-icon-btn"
        aria-label="Notifications"
        data-testid="phone-notifications-button"
        @click="notifications"
      >
        <Bell aria-hidden="true" />
      </button>
      <button
        type="button"
        class="ph-btn ph-btn--outline ph-btn--sm inbox__new"
        data-testid="phone-new-session-button"
        @click="newSession"
      >
        <Plus aria-hidden="true" />New session
      </button>
    </header>

    <div class="ph-panel">
      <!-- Needs you -->
      <div
        v-show="tab === 'needs-you'"
        class="ph-tabpage"
      >
        <PullIndicator
          ref="ptr"
          :dots="pull.dots.value"
          :refreshing="pull.refreshing.value"
        />
        <div
          ref="needsScroller"
          class="ph-scroller"
        >
          <div
            ref="needsInner"
            class="ph-scroller__inner"
          >
            <div
              ref="needsTitle"
              class="ph-page-head"
            >
              <h1>Needs you</h1>
              <p data-testid="inbox-machines-line">
                <span
                  v-for="(machine, index) in status.machines"
                  :key="machine.name"
                  class="inbox__machine"
                  :class="{ 'inbox__machine--next': index > 0 }"
                ><span
                  class="ph-mdot"
                  :class="{ 'ph-mdot--off': machine.tone === 'connecting', 'ph-mdot--bad': machine.tone === 'away' }"
                  aria-hidden="true"
                />{{ machine.name }}</span>
                <span>· {{ status.note }}</span>
              </p>
            </div>

            <template v-if="firstLoad">
              <div
                class="ph-asks"
                role="presentation"
              >
                <div
                  v-for="n in 2"
                  :key="`ask-${n}`"
                  class="ph-pcard inbox__sk-card"
                >
                  <span
                    class="ph-sk"
                    style="width: 42%"
                  />
                  <div class="inbox__sk-title">
                    <span
                      class="ph-sk"
                      style="width: 76%; height: 1em"
                    />
                  </div>
                  <div class="ph-cmd inbox__sk-cmd">
                    <span
                      class="ph-sk"
                      style="width: 88%"
                    />
                  </div>
                  <div class="ph-btns">
                    <span class="ph-sk inbox__sk-btn" />
                    <span class="ph-sk inbox__sk-btn" />
                  </div>
                </div>
              </div>
              <div class="ph-section-h">
                <span
                  class="ph-sk"
                  style="width: 76px"
                />
              </div>
              <div class="ph-rows">
                <div
                  v-for="n in 2"
                  :key="`row-${n}`"
                  class="ph-srow"
                >
                  <span class="ph-srow__g"><span class="ph-sk inbox__sk-glyph" /></span>
                  <div class="ph-srow__main">
                    <span
                      class="ph-sk"
                      style="width: 68%"
                    />
                    <div class="inbox__sk-sub">
                      <span
                        class="ph-sk"
                        style="width: 44%; height: 0.75em"
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

              <div
                v-if="inbox.needsYou.length"
                class="ph-asks"
              >
                <InboxAskRow
                  v-for="item in inbox.needsYou"
                  :key="item.key"
                  :item="item"
                  :now="now"
                  @open="open"
                  @permission="onPermission"
                  @question="onQuestion"
                />
              </div>
              <p
                v-else
                class="ph-empty ph-fade-in"
              >
                <Check aria-hidden="true" />
                <span>Nothing needs you right now.</span>
              </p>

              <template v-if="working.length">
                <h2 class="ph-section-h">
                  Working <small>{{ working.length }}</small>
                </h2>
                <div class="ph-rows">
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
                <h2 class="ph-section-h">
                  Finished <small>{{ finishedAll.length }}</small>
                  <button
                    type="button"
                    class="ph-section-h__act ph-press"
                    data-testid="inbox-all-sessions"
                    @click="select('sessions')"
                  >
                    All sessions
                  </button>
                </h2>
                <div class="ph-rows">
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
        class="ph-tabpage"
      >
        <div
          ref="sessionsScroller"
          class="ph-scroller"
        >
          <div class="ph-scroller__inner">
            <div
              ref="sessionsTitle"
              class="ph-page-head"
            >
              <h1>Sessions</h1>
              <p>{{ sessionsLine }}</p>
            </div>
            <div class="inbox__filter">
              <label class="ph-field">
                <Search aria-hidden="true" />
                <input
                  v-model="query"
                  class="phone-composer-input"
                  type="search"
                  placeholder="Filter sessions"
                  aria-label="Filter sessions"
                  enterkeyhint="search"
                  data-testid="phone-sessions-search"
                >
              </label>
            </div>
            <template
              v-for="group in byMachine"
              :key="group.machine.id"
            >
              <button
                type="button"
                class="ph-mhead"
                :class="{ 'ph-mhead--folded': folded.has(group.machine.id) }"
                :aria-expanded="!folded.has(group.machine.id)"
                data-testid="phone-machine-head"
                @click="toggleFold(group.machine.id)"
              >
                <ChevronDown
                  class="ph-mhead__chev"
                  aria-hidden="true"
                />
                <span
                  class="ph-mdot"
                  :class="{ 'ph-mdot--off': group.machine.status === 'connecting', 'ph-mdot--bad': group.machine.status === 'unreachable' }"
                  aria-hidden="true"
                />
                {{ group.machine.name }}
                <span
                  v-if="group.machine.status === 'live'"
                  class="ph-tag ph-tag--live"
                >live</span>
                <small>{{ group.items.length }}</small>
              </button>
              <div
                v-if="!folded.has(group.machine.id)"
                class="ph-rows"
              >
                <SwipeRow
                  v-for="item in group.items"
                  :key="item.key"
                  @archive="archive(item)"
                >
                  <InboxSessionRow
                    :item="item"
                    :now="now"
                    by="folder"
                    @open="open"
                  />
                </SwipeRow>
              </div>
            </template>
            <p
              v-if="!firstLoad && allSessions.length === 0"
              class="ph-empty inbox__none"
            >
              {{ query ? "No sessions match." : "No sessions in the last month." }}
            </p>
            <p class="ph-foot inbox__swipe-hint">
              Swipe a session left to archive it.
            </p>
            <div class="ph-card inbox__gap">
              <a
                class="ph-set ph-set--accent"
                href="/?view=full"
              >
                <span class="ph-set__main"><span class="ph-set__t">Open the full Fleet</span></span>
                <ExternalLink
                  class="ph-set__chev"
                  aria-hidden="true"
                />
              </a>
            </div>
          </div>
        </div>
      </div>

      <!-- Machines -->
      <div
        v-show="tab === 'machines'"
        class="ph-tabpage"
      >
        <div
          ref="machinesScroller"
          class="ph-scroller"
        >
          <div class="ph-scroller__inner">
            <div
              ref="machinesTitle"
              class="ph-page-head"
            >
              <h1>Machines</h1>
              <p>{{ machinesLine }}</p>
            </div>
            <div class="ph-card inbox__first-card">
              <button
                v-for="machine in machines"
                :key="machine.id"
                type="button"
                class="ph-set"
                data-testid="phone-machine"
                @click="showMachine(machine.name)"
              >
                <span
                  class="ph-mdot"
                  :class="{ 'ph-mdot--off': machine.status === 'connecting', 'ph-mdot--bad': machine.status === 'unreachable' || !!machine.problem }"
                  aria-hidden="true"
                />
                <span class="ph-set__main">
                  <span class="ph-set__t">
                    <span>{{ machine.name }}</span>
                    <span
                      v-if="machine.os"
                      class="ph-tag"
                    >{{ machine.os }}</span>
                    <span
                      v-if="machine.status === 'live'"
                      class="ph-tag ph-tag--live"
                    >live</span>
                  </span>
                  <span class="ph-set__s">{{ machineDetail(machine) }}</span>
                </span>
                <ChevronRight
                  class="ph-set__chev"
                  aria-hidden="true"
                />
              </button>
            </div>
            <p class="ph-foot">
              {{ homeName }} sends this phone's notifications for every machine here. Add machines from Fleet on a
              computer, in Settings&nbsp;→&nbsp;Machines.
            </p>
            <div class="ph-label">
              This phone
            </div>
            <div class="ph-card">
              <button
                type="button"
                class="ph-set"
                data-testid="phone-machines-notifications"
                @click="notifications"
              >
                <Bell
                  class="ph-set__ic"
                  aria-hidden="true"
                />
                <span class="ph-set__main"><span class="ph-set__t">Notifications</span></span>
                <span class="ph-set__v">{{ push.subscribed.value ? "On" : "Off" }}</span>
                <ChevronRight
                  class="ph-set__chev"
                  aria-hidden="true"
                />
              </button>
              <a
                class="ph-set"
                href="/pair"
                data-testid="phone-machines-pair"
              >
                <Smartphone
                  class="ph-set__ic"
                  aria-hidden="true"
                />
                <span class="ph-set__main"><span class="ph-set__t">Pair with another machine</span></span>
                <ChevronRight
                  class="ph-set__chev"
                  aria-hidden="true"
                />
              </a>
              <a
                class="ph-set"
                href="/?view=full"
              >
                <Laptop
                  class="ph-set__ic"
                  aria-hidden="true"
                />
                <span class="ph-set__main"><span class="ph-set__t">Open the full Fleet</span></span>
                <ChevronRight
                  class="ph-set__chev"
                  aria-hidden="true"
                />
              </a>
            </div>
          </div>
        </div>
      </div>
    </div>

    <PhoneTabBar
      :tab="tab"
      :needs-you="inbox.needsYou.length"
      @select="select"
    />
  </div>
</template>

<style scoped>
.inbox {
  position: absolute;
  inset: 0;
}

.inbox__new {
  margin-left: 2px;
}

.inbox__machine {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.inbox__machine--next {
  margin-left: 6px;
}

.inbox__filter {
  padding: 6px 12px 2px;
}

.inbox__none {
  margin-top: 18px;
}

.inbox__swipe-hint {
  margin-top: 14px;
}

.inbox__gap {
  margin-top: 18px;
}

.inbox__first-card {
  margin-top: 8px;
}

.inbox__sk-card {
  border-color: var(--border);
  background: var(--ph-panel);
}

.inbox__sk-title {
  margin: 14px 0 4px;
}

.inbox__sk-cmd {
  border-color: transparent;
  background: var(--ph-tint-3);
}

.inbox__sk-btn {
  height: 44px;
  border-radius: var(--ph-r-btn);
}

.inbox__sk-glyph {
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.inbox__sk-sub {
  margin-top: 8px;
}
</style>
