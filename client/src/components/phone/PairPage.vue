<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  PairingCodeGoneError,
  previewPairing,
  redeemPairing,
  type PairingCodeRef,
  type PairingPreview,
} from "@/lib/devices-api";
import { saveCredentials } from "@/lib/device-credentials";
import { fetchMachineList } from "@/lib/phone/grants";
import { useMachineGrants } from "@/composables/phone/use-machine-grants";
import { decodePairingFragment, guessDeviceName, guessPlatform, normalizeManualCode, pairingUrl, type PairingPayloadV1 } from "@/lib/pairing";

/**
 * The phone's side of pairing (`/pair`): "Connect this phone to hangar?". The QR code opens this page with the
 * one-time secret in the fragment; the page reads it, drops it from the address bar, and asks the machine which
 * machine it is. Connect redeems the code for the phone's own key. Without a fragment (opened from the Home
 * Screen, where iOS may not share Safari's storage) it asks for the code shown on the computer instead.
 */
type Step = "loading" | "code" | "confirm" | "connecting" | "gone" | "wrong-machine" | "error";

const router = useRouter();
const step = shallowRef<Step>("loading");
const code = shallowRef<PairingCodeRef | null>(null);
const payload = shallowRef<PairingPayloadV1 | null>(null);
const preview = shallowRef<PairingPreview | null>(null);
const deviceName = shallowRef(guessDeviceName(navigator.userAgent, navigator.maxTouchPoints));
const typedCode = shallowRef("");
const error = shallowRef<string | null>(null);
const grants = useMachineGrants();

const inputClass = "w-full rounded-btn border border-border bg-card-bg px-3 text-base text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const rightPlace = computed(() => {
  if (!payload.value) return null;
  try {
    return `${new URL(payload.value.url).origin}/pair`;
  } catch {
    return null;
  }
});
/** The QR link with its secret, for opening on the machine's own address. Built only on demand. */
const rightPlaceLink = computed(() => {
  return payload.value ? pairingUrl(payload.value) : null;
});

onMounted(() => {
  const fromFragment = decodePairingFragment(window.location.hash);
  if (fromFragment) {
    payload.value = fromFragment;
    // The secret has done its job in the address bar; keep it out of history and the Home Screen URL.
    window.history.replaceState(window.history.state, "", window.location.pathname);
    code.value = { secret: fromFragment.secret };
    void look();
  } else {
    step.value = "code";
  }
});

async function look(): Promise<void> {
  if (!code.value) return;
  step.value = "loading";
  error.value = null;
  try {
    preview.value = await previewPairing(code.value);
    step.value = "confirm";
  } catch (failure) {
    if (failure instanceof PairingCodeGoneError) {
      const elsewhere = payload.value && new URL(payload.value.url).origin !== window.location.origin;
      step.value = elsewhere ? "wrong-machine" : "gone";
      return;
    }
    error.value = failure instanceof Error ? failure.message : String(failure);
    step.value = "error";
  }
}

function submitCode(): void {
  const normalized = normalizeManualCode(typedCode.value);
  if (!normalized) {
    error.value = "A code is 8 letters and digits, like FDK4-9QXM.";
    return;
  }
  code.value = { manualCode: normalized };
  void look();
}

async function connect(): Promise<void> {
  if (!code.value || !preview.value) return;
  const name = deviceName.value.trim();
  if (!name) {
    error.value = "Give this phone a name.";
    return;
  }
  step.value = "connecting";
  error.value = null;
  try {
    const redeemed = await redeemPairing(code.value, name, guessPlatform(navigator.userAgent, navigator.maxTouchPoints));
    await saveCredentials({
      homeMachineId: redeemed.machine.id,
      homeMachineName: redeemed.machine.name,
      homeBaseUrl: window.location.origin,
      deviceId: redeemed.deviceId,
      token: redeemed.token,
      grants: [],
      pairedAt: new Date().toISOString(),
    });
    // Keys to the other machines in home's list come in the background; the inbox asks again for any missing.
    void fetchMachineList(redeemed.token).then((machines) => grants.ensureGrants(machines)).catch(() => undefined);
    await router.navigate({ to: "/phone/setup" });
  } catch (failure) {
    if (failure instanceof PairingCodeGoneError) {
      step.value = "gone";
      return;
    }
    error.value = failure instanceof Error ? failure.message : String(failure);
    step.value = "confirm";
  }
}

function enterCode(): void {
  code.value = null;
  payload.value = null;
  typedCode.value = "";
  error.value = null;
  step.value = "code";
}
</script>

<template>
  <main
    class="pair"
    data-testid="pair-page"
  >
    <div
      v-if="step === 'loading'"
      class="pair__center text-muted"
    >
      <LoaderCircle
        class="animate-spin"
        :size="20"
        aria-hidden="true"
      />
      <span class="sr-only">Checking the code…</span>
    </div>

    <form
      v-else-if="step === 'confirm' || step === 'connecting'"
      class="pair__body"
      @submit.prevent="connect"
    >
      <span
        class="pair__pill"
        data-testid="pair-machine"
      >{{ preview?.machineName }} · {{ preview?.os }}</span>
      <h1 class="pair__title">
        Connect this phone to {{ preview?.machineName }}?
      </h1>
      <p class="pair__text">
        This phone gets its own key to {{ preview?.machineName }}. It can start sessions, answer agents and read
        their work. Remove it any time in Settings › Machines.
      </p>
      <label
        class="pair__label"
        for="pair-device-name"
      >Name this phone</label>
      <input
        id="pair-device-name"
        v-model="deviceName"
        :class="inputClass"
        class="h-11"
        maxlength="60"
        autocomplete="off"
        data-testid="pair-device-name"
      >
      <p
        v-if="error"
        class="mt-2 text-sm text-error"
        role="alert"
      >
        {{ error }}
      </p>
      <Button
        type="submit"
        class="mt-5 h-11 w-full"
        :disabled="step === 'connecting'"
        data-testid="pair-connect"
      >
        <LoaderCircle
          v-if="step === 'connecting'"
          class="animate-spin"
          aria-hidden="true"
        />
        Connect
      </Button>
      <p class="mt-4 text-center text-xs text-muted">
        Not you? Close this page.
      </p>
    </form>

    <form
      v-else-if="step === 'code'"
      class="pair__body"
      @submit.prevent="submitCode"
    >
      <h1 class="pair__title">
        Enter the code from your computer
      </h1>
      <p class="pair__text">
        On the computer, open Settings › Machines › Add a phone. Opened Fleet from the Home Screen? Enter the code
        shown there; the camera link may not reach the Home Screen app.
      </p>
      <label
        class="pair__label"
        for="pair-code"
      >Code</label>
      <input
        id="pair-code"
        v-model="typedCode"
        :class="inputClass"
        class="h-11 font-mono uppercase tracking-widest"
        placeholder="XXXX-XXXX"
        autocomplete="one-time-code"
        autocapitalize="characters"
        spellcheck="false"
        maxlength="12"
        data-testid="pair-code"
      >
      <p
        v-if="error"
        class="mt-2 text-sm text-error"
        role="alert"
      >
        {{ error }}
      </p>
      <Button
        type="submit"
        class="mt-5 h-11 w-full"
        data-testid="pair-code-continue"
      >
        Continue
      </Button>
    </form>

    <div
      v-else-if="step === 'gone'"
      class="pair__body"
      data-testid="pair-gone"
    >
      <h1 class="pair__title">
        This code has expired or was already used
      </h1>
      <p class="pair__text">
        Ask for a new code on the computer: Settings › Machines › Add a phone. Each code works once, for 10 minutes.
      </p>
      <Button
        variant="outline"
        class="mt-5 h-11 w-full"
        @click="enterCode"
      >
        Type a code instead
      </Button>
    </div>

    <div
      v-else-if="step === 'wrong-machine'"
      class="pair__body"
      data-testid="pair-wrong-machine"
    >
      <h1 class="pair__title">
        This code is for {{ payload?.machineName }}
      </h1>
      <p class="pair__text">
        It has to be opened at {{ payload?.machineName }}'s own address, not this one.
      </p>
      <Button
        as="a"
        class="mt-5 h-11 w-full"
        :href="rightPlaceLink ?? undefined"
      >
        Open {{ rightPlace }}
      </Button>
    </div>

    <div
      v-else
      class="pair__body"
    >
      <h1 class="pair__title">
        Couldn't reach Fleet
      </h1>
      <p
        class="pair__text"
        role="alert"
      >
        {{ error }}
      </p>
      <Button
        class="mt-5 h-11 w-full"
        @click="look"
      >
        Try again
      </Button>
    </div>
  </main>
</template>

<style scoped>
.pair {
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: center;
  padding: 24px 20px 40px;
}

.pair__center {
  display: flex;
  justify-content: center;
}

.pair__body {
  width: 100%;
  max-width: 420px;
  margin: 0 auto;
}

.pair__pill {
  display: inline-block;
  padding: 2px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  font-size: 12px;
  color: var(--text);
}

.pair__title {
  margin-top: 12px;
  font-size: 24px;
  font-weight: 600;
  line-height: 1.25;
}

.pair__text {
  margin-top: 10px;
  font-size: 15px;
  line-height: 1.5;
  color: var(--muted);
}

.pair__label {
  display: block;
  margin: 20px 0 6px;
  font-size: 13px;
  color: var(--muted);
}
</style>
