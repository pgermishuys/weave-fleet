<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Check, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { usePushSubscription } from "@/composables/phone/use-push-subscription";
import { readPushEnvironment } from "@/lib/push/capabilities";
import { CHOICE_GROUPS, groupsFor, kindsFor } from "@/lib/push/subscribe";

/**
 * Turning on notifications (`/phone/setup`), step 3 of pairing in the mockup: on iPhone, add Fleet to the Home
 * Screen first; pick which notifications to get and whether to stay quiet while Fleet is open on a computer; then
 * Turn on notifications, which asks for permission (it has to be this click: iOS ignores a request that isn't).
 */
const router = useRouter();
const push = usePushSubscription();
const environment = readPushEnvironment();
const testOutcome = shallowRef<string | null>(null);
const testing = shallowRef(false);

const groups = computed(() => groupsFor(push.choices.value.kinds));

function setGroup(id: string, on: boolean): void {
  const next = on ? [...new Set([...groups.value, id])] : groups.value.filter((group) => group !== id);
  void push.updateChoices({ ...push.choices.value, kinds: kindsFor(next) });
}

function setQuiet(on: boolean): void {
  void push.updateChoices({ ...push.choices.value, quietWhenDesk: on });
}

async function turnOn(): Promise<void> {
  await push.turnOn();
}

async function test(): Promise<void> {
  testing.value = true;
  const outcome = await push.sendTest();
  testing.value = false;
  testOutcome.value = outcome === "delivered"
    ? "Sent. It should arrive in a few seconds."
    : outcome === "gone"
      ? "The browser dropped this subscription. Turn notifications on again."
      : outcome ? "The push service didn't take it. Try again in a minute." : null;
}

function done(): void {
  void router.navigate({ to: "/phone" });
}

onMounted(() => {
  void push.refresh();
});
</script>

<template>
  <main
    class="setup"
    data-testid="notification-setup"
  >
    <header class="setup__head">
      <h1 class="setup__title">
        Notifications
      </h1>
      <Button
        variant="ghost"
        size="sm"
        @click="done"
      >
        {{ push.subscribed.value ? "Done" : "Skip" }}
      </Button>
    </header>

    <section
      v-if="push.state.value === 'insecure'"
      class="setup__card"
      data-testid="setup-insecure"
    >
      <b class="setup__card-title">Notifications need HTTPS</b>
      <p class="setup__text">
        Install and notifications need HTTPS through <code>tailscale serve</code>. This page still works in the
        browser. On the computer, see docs/phone.md, then pair again from the https:// address.
      </p>
    </section>

    <section
      v-if="environment.isIos"
      class="setup__card"
      data-testid="setup-home-screen"
    >
      <b class="setup__card-title">Add Fleet to your Home Screen</b>
      <p class="setup__text">
        iPhone only sends web notifications to apps on the Home Screen. Tap <b>Share</b>, then
        <b>Add to Home Screen</b>.
      </p>
      <p
        v-if="environment.isStandalone"
        class="setup__done"
      >
        <Check
          :size="14"
          aria-hidden="true"
        /> Added. Opened from the Home Screen.
      </p>
      <p
        v-else
        class="setup__text mt-2"
      >
        Then open Fleet from its Home Screen icon to finish here. If it asks to pair again, enter the code shown on
        your computer.
      </p>
    </section>

    <section
      v-if="push.state.value === 'denied'"
      class="setup__card"
      role="alert"
      data-testid="setup-denied"
    >
      <b class="setup__card-title">Notifications are blocked</b>
      <p class="setup__text">
        This browser was told not to show Fleet's notifications. Allow them in its settings for this site, then come
        back.
      </p>
    </section>

    <section
      v-if="push.state.value === 'unsupported'"
      class="setup__card"
    >
      <b class="setup__card-title">This browser can't get notifications</b>
      <p class="setup__text">
        Use Chrome on Android, or Safari on an iPhone with Fleet added to the Home Screen.
      </p>
    </section>

    <section class="setup__list">
      <label
        v-for="group in CHOICE_GROUPS"
        :key="group.id"
        class="setup__row"
      >
        <span class="setup__row-body">
          <span class="setup__name">{{ group.label }}</span>
          <span class="setup__info">{{ group.detail }}</span>
        </span>
        <Switch
          :model-value="groups.includes(group.id)"
          :data-testid="`setup-kind-${group.id}`"
          @update:model-value="(on: boolean) => setGroup(group.id, on)"
        />
      </label>
      <label class="setup__row">
        <span class="setup__row-body">
          <span class="setup__name">Quiet while I'm at the desk</span>
          <span class="setup__info">Skip the phone when Fleet is open on a computer</span>
        </span>
        <Switch
          :model-value="push.choices.value.quietWhenDesk"
          data-testid="setup-quiet"
          @update:model-value="setQuiet"
        />
      </label>
    </section>

    <p
      v-if="push.error.value"
      class="mt-3 text-sm text-error"
      role="alert"
    >
      {{ push.error.value }}
    </p>

    <template v-if="push.subscribed.value">
      <p
        class="setup__on"
        data-testid="setup-on"
      >
        <Check
          :size="16"
          aria-hidden="true"
        /> Notifications are on for this phone.
      </p>
      <Button
        variant="outline"
        class="mt-3 h-11 w-full"
        :disabled="testing"
        data-testid="setup-test"
        @click="test"
      >
        <LoaderCircle
          v-if="testing"
          class="animate-spin"
          aria-hidden="true"
        />
        Send a test notification
      </Button>
      <p
        v-if="testOutcome"
        class="mt-2 text-sm text-muted"
        role="status"
      >
        {{ testOutcome }}
      </p>
      <Button
        class="mt-3 h-11 w-full"
        @click="done"
      >
        Done
      </Button>
    </template>
    <Button
      v-else
      class="mt-4 h-11 w-full"
      :disabled="!push.canTurnOn.value || push.busy.value"
      data-testid="setup-turn-on"
      @click="turnOn"
    >
      <LoaderCircle
        v-if="push.busy.value"
        class="animate-spin"
        aria-hidden="true"
      />
      Turn on notifications
    </Button>
  </main>
</template>

<style scoped>
.setup {
  width: 100%;
  max-width: 480px;
  margin: 0 auto;
  padding: 12px 16px 32px;
}

.setup__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 44px;
}

.setup__title {
  font-size: 20px;
  font-weight: 600;
}

.setup__card,
.setup__list {
  margin-top: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.setup__card {
  padding: 12px 14px;
}

.setup__card-title {
  font-size: 14px;
}

.setup__text {
  margin-top: 4px;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}

.setup__done {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
  font-size: 13px;
  font-weight: 500;
  color: var(--running);
}

.setup__row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 56px;
  padding: 8px 14px;
  cursor: pointer;
}

.setup__row + .setup__row {
  border-top: 1px solid var(--border);
}

.setup__row-body {
  display: grid;
  flex: 1;
  min-width: 0;
}

.setup__name {
  font-size: 15px;
}

.setup__info {
  font-size: 12px;
  color: var(--muted);
}

.setup__on {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 16px;
  font-size: 14px;
  color: var(--running);
}
</style>
