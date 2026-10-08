<script setup lang="ts">
import { computed, shallowRef, useTemplateRef, watch } from "vue";
import { Outlet, useLocation } from "@tanstack/vue-router";
import InboxPage from "@/components/phone/InboxPage.vue";
import NotificationSetup from "@/components/phone/NotificationSetup.vue";
import type { PhoneTab } from "@/components/phone/PhoneTabBar.vue";
import PhoneNewSessionPage from "@/components/phone/new/PhoneNewSessionPage.vue";
import PhoneSessionPage from "@/components/phone/session/PhoneSessionPage.vue";
import MachineScope from "@/components/layout/MachineScope.vue";
import { safariSwipedBack } from "@/composables/phone/use-phone-env";
import { stack, usePhoneNav } from "@/composables/phone/use-phone-nav";
import { PARALLAX, UNDER_DIM, useSwipeBack } from "@/composables/phone/use-swipe-back";
import { getActiveMachine, loadMachines } from "@/lib/machines";
import { targetFor } from "@/lib/machine-target";
import { EASE, EASE_OUT, animateTo, reducedMotion } from "@/lib/phone/animate";
import { popDuration } from "@/lib/phone/gestures";

/**
 * The phone app as a navigation stack, the way the phones do it: the inbox (with its three tabs) at the bottom, a
 * session pushed over it from the right, the page under it sliding a third to the left and dimming. Back pops it
 * (and a swipe from anywhere on the page). New session and Notifications are sheets over the inbox, each
 * still its own address (/phone/new, /phone/setup) so links and Back work. The routes under /phone draw nothing
 * themselves: this reads the address and draws the screens, so a session being popped stays on screen while it
 * slides away. Pages that aren't part of the stack (/phone/answered) draw as usual.
 */
type Screen =
  | { kind: "inbox" | "new" | "setup"; index: number }
  | { kind: "session"; index: number; machineId: string; sessionId: string; ask: string | undefined; key: string }
  | { kind: "other"; index: number };

const location = useLocation();
const nav = usePhoneNav();

const screen = computed<Screen>(() => {
  const path = location.value.pathname.replace(/\/+$/, "") || "/";
  const index = Number((location.value.state as { __TSR_index?: number } | undefined)?.__TSR_index ?? 0);
  if (path === "/phone") return { kind: "inbox", index };
  if (path === "/phone/new") return { kind: "new", index };
  if (path === "/phone/setup") return { kind: "setup", index };
  const session = /^\/phone\/s\/([^/]+)\/([^/]+)$/.exec(path);
  if (session) {
    const machineId = decodeURIComponent(session[1]);
    const sessionId = decodeURIComponent(session[2]);
    const ask = (location.value.search as { ask?: unknown }).ask;
    return { kind: "session", index, machineId, sessionId, ask: typeof ask === "string" ? ask : undefined, key: `${machineId}/${sessionId}` };
  }
  return { kind: "other", index };
});
const session = computed(() => (screen.value.kind === "session" ? screen.value : null));
// The session's page asks the machine in its address; home when it isn't one this phone lists.
const sessionTarget = computed(() =>
  targetFor(loadMachines().find((machine) => machine.id === session.value?.machineId) ?? null));
const onStack = computed(() => screen.value.kind !== "other");

// The tab the inbox shows: the address's on /phone, and the last one under a session or a sheet.
const lastTab = shallowRef<PhoneTab>("needs-you");
const tab = computed<PhoneTab>(() => {
  if (screen.value.kind !== "inbox") return lastTab.value;
  const asked = (location.value.search as { tab?: unknown }).tab;
  return asked === "sessions" || asked === "machines" ? asked : "needs-you";
});
watch(tab, (next) => {
  lastTab.value = next;
});
const machineForNew = computed(() => {
  const asked = (location.value.search as { machine?: unknown }).machine;
  return typeof asked === "string" && asked ? asked : undefined;
});

// A session opened on another machine works in that machine with a page load; home's inbox doesn't go under it.
const showInbox = !getActiveMachine();

// Which way the stack moved: by the router's history index, else by what came and went.
let direction: "push" | "pop" = "push";
let previous: Screen = screen.value;
const routeSheetFromApp = shallowRef(false);
watch(screen, (next) => {
  const prev = previous;
  previous = next;
  if (next.index < prev.index) direction = "pop";
  else if (next.index > prev.index) direction = "push";
  else direction = prev.kind === "session" && next.kind !== "session" ? "pop" : "push";

  if (next.kind === "session") {
    // Pushed over the inbox, or started from a New session sheet this page opened (which it replaced).
    if (prev.kind !== "session") stack.depth = next.index > prev.index || (prev.kind === "new" && routeSheetFromApp.value) ? 1 : 0;
    else if (next.index > prev.index) stack.depth += 1;
    else if (next.index < prev.index) stack.depth = Math.max(0, stack.depth - 1);
  } else {
    stack.depth = 0;
  }
  if (next.kind === "new" || next.kind === "setup") routeSheetFromApp.value = next.index > prev.index && (prev.kind === "inbox" || prev.kind === "session");
}, { flush: "pre" });

const rootRef = useTemplateRef<HTMLElement>("root");
const dimRef = useTemplateRef<HTMLElement>("dim");
const topRef = shallowRef<HTMLElement | null>(null);
// The inbox under a session is hidden (not just covered) once the push has finished, and shown again to pop.
const rootHidden = shallowRef(session.value !== null);

function resetRoot(): void {
  if (rootRef.value) rootRef.value.style.transform = "";
  if (dimRef.value) dimRef.value.style.opacity = "0";
}

async function onEnter(el: Element, done: () => void): Promise<void> {
  const top = el as HTMLElement;
  const root = rootRef.value;
  const dim = dimRef.value;
  const overInbox = !document.querySelector(".ph-screen--leaving");
  top.style.zIndex = direction === "pop" ? "1" : "2";
  if (direction === "pop") {
    // Back from a child session to its parent: the parent comes back from under it.
    top.style.transform = `translateX(${PARALLAX * 100}%)`;
    await animateTo(top, { transform: "translateX(0)" }, 380, EASE_OUT);
    top.style.transform = "";
    done();
    return;
  }
  if (reducedMotion()) {
    top.style.opacity = "0";
    await animateTo(top, { opacity: "1" }, 160, "ease-out");
    top.style.opacity = "";
    rootHidden.value = true;
    done();
    return;
  }
  top.style.transform = "translateX(100%)";
  const moves = [animateTo(top, { transform: "translateX(0)" }, 440, EASE)];
  if (overInbox && root && dim) {
    rootHidden.value = false;
    moves.push(animateTo(root, { transform: `translateX(${PARALLAX * 100}%)` }, 440, EASE), animateTo(dim, { opacity: String(UNDER_DIM) }, 440, EASE));
  }
  await Promise.all(moves);
  top.style.transform = "";
  rootHidden.value = true;
  done();
}

async function onLeave(el: Element, done: () => void): Promise<void> {
  const top = el as HTMLElement;
  top.classList.add("ph-screen--leaving");
  const from = stack.popFrom;
  stack.popFrom = null;
  const width = top.clientWidth || window.innerWidth;
  const toSession = screen.value.kind === "session";

  if (safariSwipedBack()) {
    // Safari already drew its own swipe back.
    if (!toSession) {
      rootHidden.value = false;
      resetRoot();
    }
    done();
    return;
  }
  if (toSession && direction === "push") {
    // A child session pushed over this one: this one slides a third away under it.
    top.style.zIndex = "1";
    await animateTo(top, { transform: `translateX(${PARALLAX * 100}%)` }, 440, EASE);
    done();
    return;
  }
  top.style.zIndex = "2";
  if (reducedMotion()) {
    if (!toSession) {
      rootHidden.value = false;
      resetRoot();
    }
    await animateTo(top, { opacity: "0" }, 140, "ease-out");
    done();
    return;
  }
  const ms = popDuration(width, from?.fromX ?? 0, from?.velocity ?? 0);
  const moves = [animateTo(top, { transform: `translateX(${width}px)` }, ms, EASE_OUT)];
  if (!toSession && rootRef.value && dimRef.value) {
    rootHidden.value = false;
    if (!from) {
      rootRef.value.style.transform = `translateX(${PARALLAX * 100}%)`;
      dimRef.value.style.opacity = String(UNDER_DIM);
    }
    moves.push(animateTo(rootRef.value, { transform: "translateX(0)" }, ms, EASE_OUT), animateTo(dimRef.value, { opacity: "0" }, ms, EASE_OUT));
  }
  await Promise.all(moves);
  if (!toSession) resetRoot();
  done();
}

const swipe = useSwipeBack({
  screen: topRef,
  under: () => rootRef.value,
  dim: () => dimRef.value,
  // Only where what's revealed is where Back goes: the inbox.
  enabled: () => stack.depth <= 1,
  onReveal: (revealed) => {
    rootHidden.value = !revealed;
  },
  onBack: (fromX, velocity) => {
    stack.popFrom = { fromX, velocity };
    void nav.back();
  },
});

function setTop(el: unknown): void {
  const next = (el as HTMLElement | null) ?? null;
  if (next === topRef.value) return;
  topRef.value = next;
  swipe.bind(next);
}
</script>

<template>
  <template v-if="onStack">
    <section
      ref="root"
      class="ph-screen"
      :class="{ 'ph-screen--hidden': rootHidden && session }"
      :aria-hidden="session ? 'true' : undefined"
    >
      <InboxPage
        v-if="showInbox"
        :tab="tab"
      />
      <div
        ref="dim"
        class="ph-screen__dim"
      />
    </section>
    <Transition
      :css="false"
      @enter="onEnter"
      @leave="onLeave"
    >
      <section
        v-if="session"
        :key="session.key"
        :ref="setTop"
        class="ph-screen ph-screen--pushed"
      >
        <MachineScope :target="sessionTarget">
          <PhoneSessionPage
            :machine-id="session.machineId"
            :session-id="session.sessionId"
            :ask="session.ask"
          />
        </MachineScope>
      </section>
    </Transition>
    <PhoneNewSessionPage
      :open="screen.kind === 'new'"
      :machine-id="machineForNew"
      @close="nav.closeRoute(routeSheetFromApp)"
    />
    <NotificationSetup
      :open="screen.kind === 'setup'"
      @close="nav.closeRoute(routeSheetFromApp)"
    />
  </template>
  <Outlet v-else />
</template>
