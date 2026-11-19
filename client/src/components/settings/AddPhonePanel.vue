<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef, watch } from "vue";
import { Check, Globe, Smartphone, Wifi } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import QrCode from "@/components/settings/QrCode.vue";
import ReachDialog from "@/components/settings/ReachDialog.vue";
import { useMachineReach } from "@/composables/use-machine-reach";
import { createPairingCode, NoPhoneAddressError, savePublicUrl, type PairingCodeResponse } from "@/lib/devices-api";
import { pairingAddresses } from "@/lib/pairing";
import { reachLines } from "@/lib/phone-readiness";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

/**
 * Settings → Machines → This machine → Add your phone: a one-time QR code (and a code to type) a phone scans to get
 * its own key. The code carries every address the machine can be reached on and the phone tries each, so nothing is
 * asked about addresses; "Use my own address" is for a tunnel or a domain. The code works once and expires in 10
 * minutes. The secret is only ever in the QR URL's fragment and on screen; it isn't kept anywhere.
 */
const props = defineProps<{
  machineName: string;
  publicUrl: string | null;
  addresses: readonly { url: string; kind?: string }[];
  /** Names of the devices with access now, so a new one shows up as "connected". */
  deviceNames: readonly string[];
}>();

const emit = defineEmits<{
  /** Asks the parent to re-list devices (every few seconds while the code is showing). */
  (event: "refresh"): void;
  /** The phone address was saved. */
  (event: "saved-url", url: string): void;
  /** Someone let others reach this machine (or stopped), so its addresses changed. */
  (event: "reach-changed"): void;
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

const reach = useMachineReach();
const reachOpen = shallowRef(false);
/** The desktop app listens on this machine only until someone lets others reach it: "Add your phone" asks first. */
const needsReach = computed(() => !!reach.state.value?.available && reach.state.value.mode === "off");
/** What the phone can open: the addresses Fleet listens on, else the ones just opened by letting others reach it. */
const addressesNow = computed(() => props.addresses.length ? props.addresses : reach.state.value?.listening ?? []);

/** Where the phone will reach this machine: what the code carries, else what Fleet knows now. */
const reaching = computed(() => reachLines(code.value
  ? pairingAddresses(code.value.payload)
  : [props.publicUrl, ...addressesNow.value.map((address) => address.url)].filter((url): url is string => !!url)));
/** The address a phone opens on the web to type the code: the first one the code carries. */
const firstUrl = computed(() => code.value?.payload.url ?? "");
const secondsLeft = computed(() => code.value ? Math.max(0, Math.round((Date.parse(code.value.expiresAt) - now.value) / 1000)) : 0);
const countdown = computed(() => `${Math.floor(secondsLeft.value / 60)}:${String(secondsLeft.value % 60).padStart(2, "0")}`);

function onReachChanged(): void {
  emit("reach-changed");
  // It was asked for by "Add your phone": carry on with the code.
  if (reach.state.value?.mode !== "off") void start();
}

async function start(ownAddress?: string): Promise<void> {
  if (needsReach.value) {
    reachOpen.value = true;
    return;
  }
  busy.value = true;
  error.value = null;
  connected.value = null;
  try {
    code.value = await createPairingCode(ownAddress ?? (props.publicUrl || undefined));
    devicesAtStart = [...props.deviceNames];
    now.value = Date.now();
    startTimer();
  } catch (failure) {
    if (failure instanceof NoPhoneAddressError) {
      // Fleet knows no address a phone could reach: the one thing left to ask for.
      editingUrl.value = true;
      error.value = failure.message;
    } else {
      error.value = failure instanceof Error ? failure.message : String(failure);
    }
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
    error.value = null;
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
    busy.value = false;
    return;
  }
  busy.value = false;
  // The old QR doesn't carry the new address: make a new one.
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

onMounted(() => void reach.load());
onUnmounted(stopTimer);
</script>

<template>
  <div
    class="add-phone"
    data-testid="add-phone"
  >
    <ReachDialog
      v-if="reach.state.value?.available"
      v-model:open="reachOpen"
      :machine-name="machineName"
      @changed="onReachChanged"
    />

    <div
      v-if="code"
      class="add-phone__card"
    >
      <h4 class="text-sm font-semibold text-text">
        Add your phone
      </h4>
      <ol class="mt-3 grid gap-4">
        <li>
          <p class="text-sm font-medium text-text">
            1 · Get Weave Fleet on your phone
          </p>
          <p class="mt-0.5 text-sm text-muted">
            App Store or Google Play. Search "Weave Fleet".
          </p>
        </li>
        <li>
          <p class="text-sm font-medium text-text">
            2 · Open it and scan this code
          </p>
          <div class="mt-2 flex flex-wrap items-start gap-5">
            <QrCode
              :value="code.url"
              :label="`Pairing code for ${machineName}`"
              data-testid="add-phone-qr"
            />
            <div class="min-w-0 flex-1">
              <p class="text-sm text-muted">
                Works once, for 10 minutes. No camera? Open
                <span
                  class="font-mono text-xs"
                  data-testid="add-phone-host"
                >{{ firstUrl }}/pair</span> and type
                <code
                  class="add-phone__manual"
                  data-testid="add-phone-code"
                >{{ code.manualCode }}</code>
              </p>
              <p
                class="mt-2 text-xs"
                :class="secondsLeft > 0 ? 'text-muted' : 'text-error-text'"
              >
                <template v-if="secondsLeft > 0">
                  Waiting for your phone… {{ countdown }} left
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
        </li>
      </ol>

      <div
        v-if="reaching.length"
        class="add-phone__reach"
        data-testid="add-phone-readiness"
      >
        <p class="text-sm font-medium text-text">
          Your phone will reach {{ machineName }}
        </p>
        <ul class="mt-1.5 grid gap-1">
          <li
            v-for="line in reaching"
            :key="line.id"
            class="add-phone__line"
            :data-testid="`add-phone-reach-${line.id}`"
          >
            <Wifi
              v-if="line.id === 'home'"
              :size="14"
              class="shrink-0 text-running-text"
              aria-hidden="true"
            />
            <Globe
              v-else
              :size="14"
              class="shrink-0 text-running-text"
              aria-hidden="true"
            />
            <span class="text-text">{{ line.title }}</span>
            <span class="font-mono text-xs text-muted">{{ line.hosts.join(", ") }}</span>
          </li>
        </ul>
      </div>

      <p class="mt-4 max-w-prose text-xs text-muted">
        The phone gets its own key, so removing it signs out only that phone.
      </p>

      <div class="mt-3 flex flex-wrap items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          @click="close"
        >
          Done
        </Button>
        <Button
          variant="link"
          size="sm"
          type="button"
          data-testid="add-phone-set-address"
          @click="editingUrl = !editingUrl"
        >
          Use my own address
        </Button>
      </div>
    </div>

    <div
      v-else
      class="add-phone__card"
    >
      <h4 class="text-sm font-semibold text-text">
        Use Fleet from your phone
      </h4>
      <p class="mt-1 max-w-prose text-sm text-muted">
        Answer agents, start sessions and get notifications on your phone. It connects straight to {{ machineName }},
        and nothing goes through Weave.
      </p>

      <div
        v-if="reaching.length"
        class="add-phone__reach"
        data-testid="add-phone-readiness"
      >
        <p class="text-sm font-medium text-text">
          Your phone will reach {{ machineName }}
        </p>
        <ul class="mt-1.5 grid gap-1">
          <li
            v-for="line in reaching"
            :key="line.id"
            class="add-phone__line"
            :data-testid="`add-phone-reach-${line.id}`"
          >
            <Wifi
              v-if="line.id === 'home'"
              :size="14"
              class="shrink-0 text-running-text"
              aria-hidden="true"
            />
            <Globe
              v-else
              :size="14"
              class="shrink-0 text-running-text"
              aria-hidden="true"
            />
            <span class="text-text">{{ line.title }}</span>
            <span class="font-mono text-xs text-muted">{{ line.hosts.join(", ") }}</span>
          </li>
        </ul>
      </div>
      <div class="mt-4 flex flex-wrap items-center gap-3">
        <Button
          variant="outline"
          size="sm"
          :disabled="busy"
          data-testid="add-phone-start"
          @click="start()"
        >
          <Spinner
            v-if="busy"
            size="lg"
          />
          <Smartphone
            v-else
            aria-hidden="true"
          />
          Add your phone
        </Button>
        <Button
          variant="link"
          size="sm"
          type="button"
          data-testid="add-phone-set-address"
          @click="editingUrl = !editingUrl"
        >
          Use my own address
        </Button>
      </div>
      <p
        v-if="connected"
        class="mt-2 flex items-center gap-1.5 text-sm text-running-text"
        role="status"
      >
        <Check
          :size="14"
          aria-hidden="true"
        />
        {{ connected }} is connected.
      </p>
    </div>

    <div
      v-if="editingUrl"
      class="add-phone__card mt-3"
      data-testid="add-phone-own-address"
    >
      <h4 class="text-sm font-semibold text-text">
        Use my own address
      </h4>
      <p class="mt-1 max-w-prose text-sm text-muted">
        An address you run yourself that reaches {{ machineName }}: a tunnel, or a domain such as
        https://fleet.example.com. The code carries it first, ahead of the ones Fleet found.
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
          placeholder="https://fleet.example.com"
          aria-label="Your own address"
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

    <ErrorNote
      v-if="error"
      :message="error"
      class="mt-2"
    />
  </div>
</template>

<style scoped>
.add-phone__reach {
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.add-phone__line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 8px;
  font-size: 13px;
}

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
</style>
