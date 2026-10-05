<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { Check, LoaderCircle, Plus, Share } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import { phoneLook } from "@/composables/phone/use-phone-env";
import { showToast } from "@/composables/phone/use-phone-toast";
import { usePushSubscription } from "@/composables/phone/use-push-subscription";
import { haptic } from "@/lib/phone/haptics";
import { readPushEnvironment } from "@/lib/push/capabilities";
import { CHOICE_GROUPS, groupsFor, kindsFor } from "@/lib/push/subscribe";

/**
 * Turning on notifications (`/phone/setup`, a sheet over the inbox; step 3 of pairing): what they're for, on iPhone
 * the Home Screen steps first, then Turn on notifications, which asks for permission (it has to be this tap: iOS
 * ignores a request that isn't). Once on: which ones, quiet at the desk, and a test. One Done closes it.
 */
const props = defineProps<{ open: boolean }>();
const emit = defineEmits<{ (event: "close"): void }>();

const push = usePushSubscription();
const environment = readPushEnvironment();
const testOutcome = shallowRef<string | null>(null);
const testing = shallowRef(false);

const groups = computed(() => groupsFor(push.choices.value.kinds));
const needsHomeScreen = computed(() => push.state.value === "ios-needs-install");
const deviceLine = computed(() => {
  if (environment.isIos) return environment.isStandalone ? "iPhone · Home Screen app" : "iPhone · Safari";
  return phoneLook.value === "android" ? "Android · Chrome" : "This browser";
});

/** The phone's words for each kind of notification. */
const WORDS: Record<string, { label: string; detail: string }> = {
  "needs-you": { label: "Something needs me", detail: "A command or edit waits for my approval" },
  questions: { label: "An agent asks", detail: "A question I need to answer" },
  finished: { label: "A session finishes", detail: "Its turn ended" },
  failed: { label: "A session fails", detail: "It stopped with an error" },
};

function setGroup(id: string, on: boolean): void {
  const next = on ? [...new Set([...groups.value, id])] : groups.value.filter((group) => group !== id);
  void push.updateChoices({ ...push.choices.value, kinds: kindsFor(next) });
}

function setQuiet(on: boolean): void {
  void push.updateChoices({ ...push.choices.value, quietWhenDesk: on });
}

async function turnOn(): Promise<void> {
  if (!push.canTurnOn.value) {
    if (needsHomeScreen.value) showToast("Add Fleet to your Home Screen first");
    else if (push.state.value === "denied") showToast("Allow notifications for this site in the browser's settings");
    else showToast("This browser can't get notifications");
    return;
  }
  await push.turnOn();
  if (push.subscribed.value) haptic("success");
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

watch(() => props.open, (open) => {
  if (open) void push.refresh();
}, { immediate: true });

watch(() => push.subscribed.value, () => {
  testOutcome.value = null;
});
</script>

<template>
  <BottomSheet
    :open="open"
    label="Notifications"
    :detents="['large']"
    recess
    :history="false"
    @close="emit('close')"
  >
    <template #head>
      <h2>Notifications</h2>
      <span class="ph-navbar__spacer" />
      <button
        type="button"
        class="ph-navbtn ph-glass ph-navbtn--text ph-navbtn--accent"
        data-testid="setup-done"
        @click="emit('close')"
      >
        Done
      </button>
    </template>

    <div data-testid="notification-setup">
      <template v-if="!push.subscribed.value">
        <div class="ph-sheet__pad ph-hero">
          <img
            src="/icons/apple-touch-icon.png"
            alt=""
            width="72"
            height="72"
          >
          <h3>Know when an agent needs you</h3>
          <p>Fleet taps you on the shoulder when a command waits for your approval, an agent asks a question, or a session finishes.</p>
        </div>

        <template v-if="needsHomeScreen">
          <div class="ph-group-h">
            On iPhone, first add Fleet to your Home Screen
          </div>
          <div
            class="ph-group"
            data-testid="setup-home-screen"
          >
            <div
              class="ph-row ph-row--static"
              style="--ph-sep-left: 56px"
            >
              <span class="ph-row__icon ph-row__icon--plain"><Share
                :size="22"
                aria-hidden="true"
              /></span>
              <span class="ph-row__main"><span class="ph-row__title ph-row__title--wrap">Tap Share in Safari</span></span>
            </div>
            <div
              class="ph-row ph-row--static"
              style="--ph-sep-left: 56px"
            >
              <span class="ph-row__icon ph-row__icon--plain"><Plus
                :size="22"
                aria-hidden="true"
              /></span>
              <span class="ph-row__main"><span class="ph-row__title ph-row__title--wrap">Choose Add to Home Screen</span></span>
            </div>
            <div
              class="ph-row ph-row--static"
              style="--ph-sep-left: 56px"
            >
              <span class="ph-row__icon ph-row__icon--plain"><img
                src="/icons/apple-touch-icon.png"
                alt=""
                width="24"
                height="24"
                class="setup__mini-icon"
              ></span>
              <span class="ph-row__main"><span class="ph-row__title ph-row__title--wrap">Open Fleet from the Home Screen</span></span>
            </div>
          </div>
          <p class="ph-group-f">
            iOS only lets web apps send notifications once they're on the Home Screen. If it asks to pair again there,
            enter the code shown on your computer.
          </p>
        </template>

        <p
          v-if="push.state.value === 'insecure'"
          class="ph-note ph-note--warn setup__note"
          data-testid="setup-insecure"
        >
          Notifications need HTTPS, through <code>tailscale serve</code>. This page still works in the browser. On the
          computer, see docs/phone.md, then pair again from the https:// address.
        </p>
        <p
          v-else-if="push.state.value === 'denied'"
          class="ph-note ph-note--warn setup__note"
          role="alert"
          data-testid="setup-denied"
        >
          This browser was told not to show Fleet's notifications. Allow them in its settings for this site, then come
          back.
        </p>
        <p
          v-else-if="push.state.value === 'unsupported'"
          class="ph-note setup__note"
        >
          This browser can't get notifications. Use Chrome on Android, or Safari on an iPhone with Fleet on the Home
          Screen.
        </p>

        <div class="ph-sheet__pad setup__turn-on">
          <button
            type="button"
            class="ph-btn ph-btn--primary ph-btn--big"
            :disabled="push.busy.value"
            data-testid="setup-turn-on"
            @click="turnOn"
          >
            <LoaderCircle
              v-if="push.busy.value"
              class="ph-spinner"
              :size="20"
              aria-hidden="true"
            />
            <span>{{ push.busy.value ? "Asking…" : "Turn on notifications" }}</span>
          </button>
        </div>
      </template>

      <template v-else>
        <div class="ph-group ph-fade-in">
          <div
            class="ph-row ph-row--static"
            data-testid="setup-on"
          >
            <span class="ph-row__icon setup__on-icon"><Check
              :size="22"
              :stroke-width="2.6"
              aria-hidden="true"
            /></span>
            <span class="ph-row__main">
              <span class="ph-row__title">On for this phone</span>
              <span class="ph-row__sub">{{ deviceLine }}</span>
            </span>
          </div>
        </div>
        <div class="ph-group-h">
          Tell me when
        </div>
        <div class="ph-group ph-fade-in">
          <button
            v-for="group in CHOICE_GROUPS"
            :key="group.id"
            type="button"
            class="ph-row"
            role="switch"
            :aria-checked="groups.includes(group.id)"
            :data-testid="`setup-kind-${group.id}`"
            @click="setGroup(group.id, !groups.includes(group.id))"
          >
            <span class="ph-row__main">
              <span class="ph-row__title">{{ WORDS[group.id]?.label ?? group.label }}</span>
              <span class="ph-row__sub ph-row__sub--wrap">{{ WORDS[group.id]?.detail ?? group.detail }}</span>
            </span>
            <span
              class="ph-switch"
              :class="{ 'ph-switch--on': groups.includes(group.id) }"
              aria-hidden="true"
            />
          </button>
        </div>
        <div class="ph-group ph-fade-in setup__gap">
          <button
            type="button"
            class="ph-row"
            role="switch"
            :aria-checked="push.choices.value.quietWhenDesk"
            data-testid="setup-quiet"
            @click="setQuiet(!push.choices.value.quietWhenDesk)"
          >
            <span class="ph-row__main">
              <span class="ph-row__title">Quiet at my desk</span>
              <span class="ph-row__sub ph-row__sub--wrap">Skip the phone while Fleet is open on a computer</span>
            </span>
            <span
              class="ph-switch"
              :class="{ 'ph-switch--on': push.choices.value.quietWhenDesk }"
              aria-hidden="true"
            />
          </button>
        </div>
        <div class="ph-group setup__gap">
          <button
            type="button"
            class="ph-row ph-row--accent"
            :disabled="testing"
            data-testid="setup-test"
            @click="test"
          >
            <span class="ph-row__main"><span class="ph-row__title">Send a test notification</span></span>
            <LoaderCircle
              v-if="testing"
              class="ph-spinner"
              :size="18"
              aria-hidden="true"
            />
          </button>
        </div>
        <p
          class="ph-group-f"
          role="status"
        >
          {{ testOutcome ?? "It arrives like a real one, so you can see how it looks." }}
        </p>
      </template>

      <p
        v-if="push.error.value"
        class="ph-note ph-note--error setup__note"
        role="alert"
      >
        {{ push.error.value }}
      </p>
    </div>
  </BottomSheet>
</template>

<style scoped>
.ph-row__title,
.ph-row__sub {
  display: block;
}

.setup__mini-icon {
  border-radius: 6px;
}

.setup__note {
  margin: 18px 32px 0;
}

.setup__turn-on {
  margin-top: 24px;
}

.setup__on-icon {
  background: var(--running);
}

.setup__gap {
  margin-top: 22px;
}
</style>
