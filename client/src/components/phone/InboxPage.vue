<script setup lang="ts">
import { computed } from "vue";
import { useRouter, useSearch } from "@tanstack/vue-router";
import { Bell, LayoutDashboard, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import InboxAskRow from "@/components/phone/InboxAskRow.vue";
import InboxSessionRow from "@/components/phone/InboxSessionRow.vue";
import PhoneTabBar, { type PhoneTab } from "@/components/phone/PhoneTabBar.vue";
import { useInbox } from "@/composables/phone/use-inbox";
import { readCredentialsSync } from "@/lib/device-credentials";
import { rememberPhoneMachine } from "@/lib/machines";
import { buildInbox, type InboxItem } from "@/lib/phone/inbox";
import { ago, clock } from "@/lib/phone/time";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";

/**
 * The phone's home (`/phone`): what needs you on every machine, answered from the list; then what's working and what
 * finished. Sessions lists everything; Machines says which machines the phone can reach.
 */
const router = useRouter();
const search = useSearch({ from: "/phone/" });
const { inbox, machines, loading, now, answerPermission, answerQuestion, targetFor } = useInbox();

const tab = computed<PhoneTab>(() => search.value.tab ?? "needs-you");
const machineCount = computed(() => machines.value.length);
const unreachable = computed(() => machines.value.filter((m) => m.status === "unreachable" && !m.problem));
/** Every session from the last 30 days, newest first. */
const allSessions = computed(() => {
  const built = buildInbox(machines.value, now.value, 30 * 24 * 3_600_000);
  return [...built.needsYou, ...built.working, ...built.finished].sort((a, b) => b.updatedAt - a.updatedAt);
});

function select(next: PhoneTab): void {
  void router.navigate({ to: "/phone", search: next === "needs-you" ? {} : { tab: next } });
}

function open(item: InboxItem): void {
  const path = `/phone/s/${encodeURIComponent(item.machineId)}/${encodeURIComponent(item.sessionId)}`;
  const home = machines.value.find((m) => m.isHome);
  if (item.machineId === home?.id) {
    void router.navigate({ to: "/phone/s/$machineId/$sessionId", params: { machineId: item.machineId, sessionId: item.sessionId } });
    return;
  }

  // Another machine: the page reloads to work there, with the phone's own key for it.
  const target = targetFor(item.machineId);
  if (!target?.token) return;
  rememberPhoneMachine({ id: item.machineId, name: item.machineName, baseUrl: target.baseUrl, token: target.token, addedAt: new Date().toISOString() });
  window.location.assign(path);
}

async function onPermission(item: InboxItem, reply: PermissionReply, message: string | undefined, done: (outcome: AnswerOutcome) => void): Promise<void> {
  if (item.ask?.kind !== "permission") return;
  done(await answerPermission(item.machineId, item.ask.ask.sessionId, item.ask.ask.id, reply, message));
}

async function onQuestion(item: InboxItem, answers: string[][], done: (outcome: AnswerOutcome) => void): Promise<void> {
  if (item.ask?.kind !== "question") return;
  done(await answerQuestion(item.machineId, item.sessionId, item.ask.requestId, answers));
}

const homeName = computed(() => readCredentialsSync()?.homeMachineName ?? machines.value.find((m) => m.isHome)?.name ?? "");
</script>

<template>
  <div
    class="inbox"
    data-testid="phone-inbox"
  >
    <header class="inbox__head">
      <h1 class="inbox__title">
        Fleet
      </h1>
      <span class="inbox__sub">{{ machineCount }} machine{{ machineCount === 1 ? "" : "s" }}</span>
      <Button
        variant="toolbar-icon"
        size="icon"
        aria-label="Notifications"
        @click="router.navigate({ to: '/phone/setup' })"
      >
        <Bell :size="18" />
      </Button>
    </header>

    <main class="inbox__scroll">
      <div
        v-if="loading"
        class="inbox__empty"
        role="status"
      >
        <LoaderCircle
          class="animate-spin"
          :size="20"
          aria-hidden="true"
        />
      </div>

      <template v-else-if="tab === 'needs-you'">
        <p
          v-for="machine in unreachable"
          :key="machine.id"
          class="inbox__away"
          data-testid="inbox-unreachable"
        >
          {{ machine.name }} unreachable<template v-if="machine.lastHeardAt">
            since {{ clock(machine.lastHeardAt) }}
          </template>. Showing what it said last.
        </p>

        <h2 class="inbox__label">
          Needs you <span class="inbox__count">{{ inbox.needsYou.length }}</span>
        </h2>
        <p
          v-if="inbox.needsYou.length === 0"
          class="inbox__quiet"
        >
          Nothing needs you right now.
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

        <template v-if="inbox.working.length">
          <h2 class="inbox__label">
            Working <span class="inbox__count inbox__count--plain">{{ inbox.working.length }}</span>
          </h2>
          <div class="inbox__card">
            <InboxSessionRow
              v-for="item in inbox.working"
              :key="item.key"
              :item="item"
              :now="now"
              @open="open"
            />
          </div>
        </template>

        <template v-if="inbox.finished.length">
          <h2 class="inbox__label">
            Finished <span class="inbox__count inbox__count--plain">{{ inbox.finished.length }}</span>
          </h2>
          <div class="inbox__card">
            <InboxSessionRow
              v-for="item in inbox.finished"
              :key="item.key"
              :item="item"
              :now="now"
              @open="open"
            />
          </div>
        </template>
      </template>

      <template v-else-if="tab === 'sessions'">
        <h2 class="inbox__label">
          Sessions <span class="inbox__count inbox__count--plain">{{ allSessions.length }}</span>
        </h2>
        <div
          v-if="allSessions.length"
          class="inbox__card"
        >
          <InboxSessionRow
            v-for="item in allSessions"
            :key="item.key"
            :item="item"
            :now="now"
            @open="open"
          />
        </div>
        <p
          v-else
          class="inbox__quiet"
        >
          No sessions in the last month.
        </p>
        <Button
          as="a"
          href="/"
          variant="outline"
          class="mt-2 h-11 w-full"
        >
          <LayoutDashboard aria-hidden="true" />
          Open the full Fleet to start a session
        </Button>
      </template>

      <template v-else>
        <h2 class="inbox__label">
          Machines
        </h2>
        <div class="inbox__card">
          <div
            v-for="machine in machines"
            :key="machine.id"
            class="inbox__machine"
            data-testid="phone-machine"
          >
            <span
              class="inbox__mdot"
              :class="`inbox__mdot--${machine.status}`"
              aria-hidden="true"
            />
            <span class="inbox__mbody">
              <span class="inbox__mname">{{ machine.name }}</span>
              <span class="inbox__minfo">
                <template v-if="machine.isHome">Home · sends notifications</template>
                <template v-else-if="machine.problem">{{ machine.problem }}</template>
                <template v-else-if="machine.status === 'unreachable'">Unreachable · last heard {{ ago(machine.lastHeardAt, now) || "never" }}</template>
                <template v-else-if="machine.status === 'polling'">Checking every 15 s</template>
                <template v-else-if="machine.status === 'live'">Live</template>
                <template v-else>Connecting…</template>
              </span>
            </span>
          </div>
        </div>
        <p class="inbox__quiet">
          {{ homeName }} sends this phone's notifications for every machine here. Add machines on a computer, in
          Settings › Machines.
        </p>
      </template>
    </main>

    <PhoneTabBar
      :tab="tab"
      :needs-you="inbox.needsYou.length"
      @select="select"
    />
  </div>
</template>

<style scoped>
.inbox {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  height: 100dvh;
}

.inbox__head {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  min-height: 52px;
  padding: 4px 8px 4px 16px;
}

.inbox__title {
  flex: 1;
  font-size: 20px;
  font-weight: 600;
}

.inbox__sub {
  font-size: 12px;
  color: var(--muted);
}

.inbox__scroll {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
  overflow-y: auto;
  padding: 4px 12px 16px;
}

.inbox__label {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 2px 0;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--muted);
}

.inbox__count {
  padding: 0 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--idle) 14%, transparent);
  color: var(--idle);
  letter-spacing: 0;
}

.inbox__count--plain {
  background: var(--accent-dim);
  color: var(--muted);
}

.inbox__card {
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  background: var(--card-bg);
}

.inbox__quiet {
  padding: 4px 2px;
  font-size: 13px;
  color: var(--muted);
}

.inbox__away {
  padding: 8px 12px;
  border-radius: var(--radius-btn);
  background: var(--accent-dim);
  font-size: 12px;
  color: var(--muted);
}

.inbox__empty {
  display: grid;
  flex: 1;
  place-items: center;
  color: var(--muted);
}

.inbox__machine {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 52px;
  padding: 8px 12px;
}

.inbox__machine + .inbox__machine {
  border-top: 1px solid var(--border);
}

.inbox__mdot {
  width: 8px;
  height: 8px;
  flex: none;
  border-radius: 50%;
  background: var(--muted);
}

.inbox__mdot--live {
  background: var(--running);
}

.inbox__mdot--polling {
  background: var(--complete);
}

.inbox__mdot--unreachable {
  background: var(--error);
}

.inbox__mbody {
  display: grid;
  min-width: 0;
}

.inbox__mname {
  font-size: 14px;
}

.inbox__minfo {
  font-size: 12px;
  color: var(--muted);
}
</style>
