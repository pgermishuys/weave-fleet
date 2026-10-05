<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { LoaderCircle, Monitor } from "lucide-vue-next";
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
    class="ph-page pair"
    data-testid="pair-page"
  >
    <div
      v-if="step === 'loading'"
      class="pair__center"
      role="status"
    >
      <LoaderCircle
        class="ph-spinner"
        :size="24"
        aria-hidden="true"
      />
      <span class="sr-only">Checking the code…</span>
    </div>

    <form
      v-else-if="step === 'confirm' || step === 'connecting'"
      class="pair__body"
      @submit.prevent="connect"
    >
      <div class="ph-sheet__pad ph-hero">
        <img
          src="/icons/apple-touch-icon.png"
          alt=""
          width="72"
          height="72"
        >
        <p class="pair__machine">
          <span
            class="ph-machine"
            data-testid="pair-machine"
          ><Monitor
            :size="14"
            aria-hidden="true"
          />{{ preview?.machineName }} · {{ preview?.os }}</span>
        </p>
        <h3>Connect this phone to {{ preview?.machineName }}?</h3>
        <p>
          This phone gets its own key to {{ preview?.machineName }}. It can start sessions, answer agents and read
          their work. Remove it any time in Settings › Machines.
        </p>
      </div>
      <label
        class="ph-group-h pair__label"
        for="pair-device-name"
      >Name this phone</label>
      <div class="ph-group">
        <input
          id="pair-device-name"
          v-model="deviceName"
          class="phone-composer-input ph-field"
          maxlength="60"
          autocomplete="off"
          enterkeyhint="go"
          data-testid="pair-device-name"
        >
      </div>
      <p
        v-if="error"
        class="ph-group-f ph-note--error"
        role="alert"
      >
        {{ error }}
      </p>
      <div class="ph-sheet__pad pair__actions">
        <button
          type="submit"
          class="ph-btn ph-btn--primary ph-btn--big"
          :disabled="step === 'connecting'"
          data-testid="pair-connect"
        >
          <LoaderCircle
            v-if="step === 'connecting'"
            class="ph-spinner"
            :size="20"
            aria-hidden="true"
          />
          <span>{{ step === "connecting" ? "Connecting…" : "Connect" }}</span>
        </button>
        <p class="pair__fine">
          Not you? Close this page.
        </p>
      </div>
    </form>

    <form
      v-else-if="step === 'code'"
      class="pair__body"
      @submit.prevent="submitCode"
    >
      <div class="ph-sheet__pad ph-hero">
        <img
          src="/icons/apple-touch-icon.png"
          alt=""
          width="72"
          height="72"
        >
        <h3>Enter the code from your computer</h3>
        <p>
          On the computer, open Settings › Machines › Add a phone. Opened Fleet from the Home Screen? Enter the code
          shown there; the camera link may not reach the Home Screen app.
        </p>
      </div>
      <label
        class="ph-group-h pair__label"
        for="pair-code"
      >Code</label>
      <div class="ph-group">
        <input
          id="pair-code"
          v-model="typedCode"
          class="phone-composer-input ph-field pair__code"
          placeholder="XXXX-XXXX"
          autocomplete="one-time-code"
          autocapitalize="characters"
          spellcheck="false"
          maxlength="12"
          enterkeyhint="go"
          data-testid="pair-code"
        >
      </div>
      <p
        v-if="error"
        class="ph-group-f ph-note--error"
        role="alert"
      >
        {{ error }}
      </p>
      <div class="ph-sheet__pad pair__actions">
        <button
          type="submit"
          class="ph-btn ph-btn--primary ph-btn--big"
          data-testid="pair-code-continue"
        >
          <span>Continue</span>
        </button>
      </div>
    </form>

    <div
      v-else-if="step === 'gone'"
      class="pair__body"
      data-testid="pair-gone"
    >
      <div class="ph-sheet__pad ph-hero">
        <h3>This code has expired or was already used</h3>
        <p>
          Ask for a new code on the computer: Settings › Machines › Add a phone. Each code works once, for 10 minutes.
        </p>
      </div>
      <div class="ph-sheet__pad pair__actions">
        <button
          type="button"
          class="ph-btn ph-btn--big"
          @click="enterCode"
        >
          <span>Type a code instead</span>
        </button>
      </div>
    </div>

    <div
      v-else-if="step === 'wrong-machine'"
      class="pair__body"
      data-testid="pair-wrong-machine"
    >
      <div class="ph-sheet__pad ph-hero">
        <h3>This code is for {{ payload?.machineName }}</h3>
        <p>It has to be opened at {{ payload?.machineName }}'s own address, not this one.</p>
      </div>
      <div class="ph-sheet__pad pair__actions">
        <a
          class="ph-btn ph-btn--primary ph-btn--big pair__link"
          :href="rightPlaceLink ?? undefined"
        ><span>Open {{ rightPlace }}</span></a>
      </div>
    </div>

    <div
      v-else
      class="pair__body"
    >
      <div class="ph-sheet__pad ph-hero">
        <h3>Couldn't reach Fleet</h3>
        <p role="alert">
          {{ error }}
        </p>
      </div>
      <div class="ph-sheet__pad pair__actions">
        <button
          type="button"
          class="ph-btn ph-btn--primary ph-btn--big"
          @click="look"
        >
          <span>Try again</span>
        </button>
      </div>
    </div>
  </main>
</template>

<style scoped>
.pair {
  display: flex;
  flex-direction: column;
  justify-content: center;
}

.pair__center {
  display: grid;
  flex: 1;
  place-items: center;
}

.pair__body {
  width: 100%;
  max-width: 480px;
  margin: 0 auto;
  padding-bottom: 24px;
}

.pair__machine {
  margin: 14px 0 0;
}

.ph-hero .pair__machine + h3 {
  margin-top: 6px;
}

.pair__label {
  display: block;
}

.pair__code {
  font-family: var(--ph-mono);
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

.pair__actions {
  margin-top: 24px;
}

.pair__fine {
  margin: 14px 0 0;
  font-size: var(--ph-t-foot);
  text-align: center;
  color: var(--muted);
}

.pair__link {
  text-decoration: none;
}
</style>
