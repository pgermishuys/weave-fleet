<script setup lang="ts">
import { computed, onUnmounted, shallowRef, watch } from "vue";
import { AlertCircle, Check, LoaderCircle, Smartphone } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import QrCode from "@/components/settings/QrCode.vue";
import { createPairingCode, savePublicUrl, type PairingCodeResponse } from "@/lib/devices-api";
import { choosePhoneBaseUrl, supportsInstall } from "@/lib/pairing";

/**
 * Settings → Machines → This machine → Add a phone: a one-time QR code (and a code to type) a phone scans to get
 * its own key. The code works once and expires in 10 minutes. The secret is only ever in the QR URL's fragment
 * and on screen; it isn't kept anywhere.
 */
const props = defineProps<{
  machineName: string;
  publicUrl: string | null;
  addresses: readonly { url: string }[];
  /** Names of the devices with access now, so a new one shows up as "connected". */
  deviceNames: readonly string[];
}>();

const emit = defineEmits<{
  /** Asks the parent to re-list devices (every few seconds while the code is showing). */
  (event: "refresh"): void;
  /** The phone address was saved. */
  (event: "saved-url", url: string): void;
}>();

const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const code = shallowRef<PairingCodeResponse | null>(null);
const busy = shallowRef(false);
const error = shallowRef<string | null>(null);
const connected = shallowRef<string | null>(null);
const now = shallowRef(Date.now());
const phoneUrl = shallowRef("");
const editingUrl = shallowRef(false);
let devicesAtStart: string[] = [];
let timer: ReturnType<typeof setInterval> | null = null;

const suggestedUrl = computed(() => choosePhoneBaseUrl(props.publicUrl, window.location, props.addresses));
const host = computed(() => {
  try {
    return code.value ? new URL(code.value.payload.url).host : "";
  } catch {
    return "";
  }
});
const secondsLeft = computed(() => code.value ? Math.max(0, Math.round((Date.parse(code.value.expiresAt) - now.value) / 1000)) : 0);
const countdown = computed(() => `${Math.floor(secondsLeft.value / 60)}:${String(secondsLeft.value % 60).padStart(2, "0")}`);
const insecure = computed(() => !!code.value && !supportsInstall(code.value.payload.url));

async function start(baseUrl = phoneUrl.value || suggestedUrl.value || ""): Promise<void> {
  if (!baseUrl) {
    // Nothing a phone could open is known yet (a loopback page, no saved address): ask for it first.
    editingUrl.value = true;
    return;
  }
  busy.value = true;
  error.value = null;
  connected.value = null;
  try {
    code.value = await createPairingCode(baseUrl);
    phoneUrl.value = code.value.payload.url;
    devicesAtStart = [...props.deviceNames];
    now.value = Date.now();
    startTimer();
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    busy.value = false;
  }
}

async function saveUrl(): Promise<void> {
  busy.value = true;
  error.value = null;
  try {
    await savePublicUrl(phoneUrl.value.trim());
    emit("saved-url", phoneUrl.value.trim());
    editingUrl.value = false;
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
    busy.value = false;
    return;
  }
  busy.value = false;
  // The old QR points at the old address: make a new one.
  await start(phoneUrl.value.trim());
}

function close(): void {
  code.value = null;
  editingUrl.value = false;
  stopTimer();
}

function startTimer(): void {
  stopTimer();
  let ticks = 0;
  timer = setInterval(() => {
    now.value = Date.now();
    if (secondsLeft.value <= 0) {
      stopTimer();
      return;
    }
    if (++ticks % 3 === 0) emit("refresh");
  }, 1000);
}

function stopTimer(): void {
  if (timer) clearInterval(timer);
  timer = null;
}

watch(() => props.deviceNames, (names) => {
  if (!code.value) return;
  const added = names.find((name) => !devicesAtStart.includes(name));
  if (!added) return;
  connected.value = added;
  close();
});

onUnmounted(stopTimer);
</script>

<template>
  <div
    class="add-phone"
    data-testid="add-phone"
  >
    <div
      v-if="!code && editingUrl"
      class="add-phone__card"
    >
      <h4 class="text-sm font-semibold text-text">
        Which address will the phone open?
      </h4>
      <p class="mt-1 max-w-prose text-sm text-muted">
        This page is on this computer only. Give the address a phone reaches {{ machineName }} at — with
        <code>tailscale serve</code> that's its https://….ts.net address.
      </p>
      <form
        class="mt-3 flex flex-wrap items-center gap-2"
        @submit.prevent="saveUrl"
      >
        <input
          v-model="phoneUrl"
          :class="inputClass"
          class="max-w-md"
          type="url"
          placeholder="https://hangar.tail9c2e.ts.net"
          aria-label="Address the phone opens"
          data-testid="add-phone-url"
        >
        <Button
          type="submit"
          size="sm"
          :disabled="busy || !phoneUrl.trim()"
        >
          Show the code
        </Button>
        <Button
          variant="ghost"
          size="sm"
          type="button"
          @click="editingUrl = false"
        >
          Cancel
        </Button>
      </form>
    </div>

    <template v-else-if="!code">
      <div class="flex flex-wrap items-center gap-3">
        <Button
          variant="outline"
          size="sm"
          :disabled="busy"
          data-testid="add-phone-start"
          @click="start()"
        >
          <LoaderCircle
            v-if="busy"
            class="animate-spin"
            aria-hidden="true"
          />
          <Smartphone
            v-else
            aria-hidden="true"
          />
          Add a phone
        </Button>
        <span class="text-xs text-muted">The phone gets its own key, so removing it signs out only that phone.</span>
      </div>
      <p
        v-if="connected"
        class="mt-2 flex items-center gap-1.5 text-sm text-running"
        role="status"
      >
        <Check
          :size="14"
          aria-hidden="true"
        />
        {{ connected }} is connected.
      </p>
    </template>

    <div
      v-else
      class="add-phone__card"
    >
      <div class="flex flex-wrap items-start gap-5">
        <QrCode
          :value="code.url"
          :label="`Pairing code for ${machineName}`"
          data-testid="add-phone-qr"
        />
        <div class="min-w-0 flex-1">
          <h4 class="text-sm font-semibold text-text">
            Add a phone
          </h4>
          <p class="mt-1 text-sm text-muted">
            Scan with the phone's camera. The code works once and expires in 10 minutes.
          </p>
          <p
            class="mt-2 font-mono text-xs text-muted"
            data-testid="add-phone-host"
          >
            {{ host }}
          </p>
          <p class="mt-3 text-sm text-text">
            No camera? Open <span class="font-mono text-xs">{{ code.payload.url }}/pair</span> and type
            <code
              class="add-phone__manual"
              data-testid="add-phone-code"
            >{{ code.manualCode }}</code>
          </p>
          <p
            class="mt-2 text-xs"
            :class="secondsLeft > 0 ? 'text-muted' : 'text-error'"
          >
            <template v-if="secondsLeft > 0">
              Expires in {{ countdown }}
            </template>
            <template v-else>
              This code has expired.
              <Button
                variant="link"
                size="sm"
                class="h-auto p-0"
                @click="start()"
              >
                Make a new one
              </Button>
            </template>
          </p>
        </div>
      </div>

      <div class="mt-4 grid gap-1">
        <span class="text-xs font-medium uppercase tracking-wide text-muted">Phone opens</span>
        <div
          v-if="!editingUrl"
          class="flex flex-wrap items-center gap-2"
        >
          <code class="add-phone__url">{{ code.payload.url }}</code>
          <Button
            variant="ghost"
            size="sm"
            @click="editingUrl = true"
          >
            Change
          </Button>
        </div>
        <form
          v-else
          class="flex flex-wrap items-center gap-2"
          @submit.prevent="saveUrl"
        >
          <input
            v-model="phoneUrl"
            :class="inputClass"
            class="max-w-md"
            type="url"
            placeholder="https://hangar.tail9c2e.ts.net"
            aria-label="Address the phone opens"
            data-testid="add-phone-url"
          >
          <Button
            type="submit"
            size="sm"
            :disabled="busy || !phoneUrl.trim()"
          >
            Save
          </Button>
          <Button
            variant="ghost"
            size="sm"
            type="button"
            @click="editingUrl = false"
          >
            Cancel
          </Button>
        </form>
        <p
          v-if="insecure"
          class="mt-1 flex max-w-prose items-start gap-1.5 text-xs text-idle"
          data-testid="add-phone-insecure"
        >
          <AlertCircle
            :size="14"
            class="mt-px shrink-0"
            aria-hidden="true"
          />
          <span>Install and notifications need HTTPS — put <code>tailscale serve</code> in front of Fleet and use its https:// address (see docs/phone.md). Over plain http the phone can still use Fleet in its browser.</span>
        </p>
      </div>

      <div class="mt-4">
        <Button
          variant="ghost"
          size="sm"
          @click="close"
        >
          Done
        </Button>
      </div>
    </div>

    <p
      v-if="error"
      class="mt-2 text-sm text-error"
      role="alert"
    >
      {{ error }}
    </p>
  </div>
</template>

<style scoped>
.add-phone__card {
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.add-phone__manual {
  padding: 1px 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-family: var(--font-mono-stack);
  font-size: 13px;
  letter-spacing: 0.08em;
}

.add-phone__url {
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--text);
}
</style>
