<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { Globe, Wifi } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { ErrorNote } from "@/components/ui/error-note";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/spinner";
import { useMachineReach } from "@/composables/use-machine-reach";
import { reachChoices } from "@/lib/reach-choices";
import type { ReachMode } from "@/lib/reach-api";

/**
 * "Let others reach studio?": the desktop app listens only on this machine until someone asks. The choices are
 * Tailscale and the home network, the home network alone, or an address of your own. Whatever is picked, every device
 * that connects needs its own key; the window itself keeps signing in on this machine without one.
 */
const props = defineProps<{
  open: boolean;
  machineName: string;
}>();

const emit = defineEmits<{
  "update:open": [value: boolean];
  /** Who can reach the app changed. */
  changed: [];
}>();

const reach = useMachineReach();

const choice = shallowRef<ReachMode>("tailscale-and-home");
const ownAddress = shallowRef("");
const busy = shallowRef(false);
const error = shallowRef<string | null>(null);

const choices = computed(() => reachChoices(reach.state.value?.found ?? []));
const ownPort = computed(() => reach.state.value?.ownPort ?? null);
const isOn = computed(() => reach.state.value?.mode !== undefined && reach.state.value.mode !== "off");
const canTurnOn = computed(() => {
  const picked = choices.value.find((entry) => entry.mode === choice.value);
  if (!picked || picked.disabled) return false;
  return choice.value !== "own" || ownAddress.value.trim().length > 0;
});

watch(() => props.open, async (open) => {
  if (!open) return;
  error.value = null;
  await reach.load();
  const current = reach.state.value;
  ownAddress.value = current?.ownAddress ?? "";
  // The recommended choice first; on again, the one in force.
  choice.value = current && current.mode !== "off" ? current.mode : choices.value.find((entry) => !entry.disabled)?.mode ?? "tailscale-and-home";
}, { immediate: true });

async function apply(mode: ReachMode): Promise<void> {
  busy.value = true;
  error.value = null;
  try {
    await reach.set(mode, mode === "own" ? ownAddress.value.trim() : undefined);
    emit("changed");
    emit("update:open", false);
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <Dialog
    :open="open"
    @update:open="emit('update:open', $event)"
  >
    <DialogContent
      class="sm:max-w-lg"
      data-testid="reach-dialog"
    >
      <DialogHeader>
        <DialogTitle>Let others reach {{ machineName }}?</DialogTitle>
        <DialogDescription>
          {{ machineName }} will listen on these addresses too. Anything that connects needs its own key, which you
          give it by adding it here, so being on the same network isn't enough. This window keeps signing in without one.
        </DialogDescription>
      </DialogHeader>

      <div
        class="grid gap-2"
        role="radiogroup"
        aria-label="Who can reach this machine"
      >
        <label
          v-for="entry in choices"
          :key="entry.mode"
          class="reach-choice"
          :class="{ 'reach-choice--on': choice === entry.mode, 'reach-choice--off': entry.disabled }"
        >
          <input
            v-model="choice"
            type="radio"
            name="reach-mode"
            class="mt-1"
            :value="entry.mode"
            :disabled="entry.disabled || busy"
            :data-testid="`reach-choice-${entry.mode}`"
          >
          <span class="min-w-0 flex-1">
            <b class="text-sm text-text">{{ entry.label }}</b>
            <span
              v-if="entry.recommended"
              class="ml-1.5 rounded-full border border-border px-2 py-px text-xs text-running-text"
            >recommended</span>
            <small class="mt-0.5 block text-xs text-muted">{{ entry.hint(machineName) }}</small>
            <span
              v-if="entry.addresses.length"
              class="mt-1.5 flex flex-wrap gap-1.5"
            >
              <span
                v-for="address in entry.addresses"
                :key="address.url"
                class="reach-chip rounded-full"
                :data-testid="`reach-address-${address.kind}`"
              >
                <Globe
                  v-if="address.kind === 'tailnet'"
                  :size="12"
                  aria-hidden="true"
                />
                <Wifi
                  v-else
                  :size="12"
                  aria-hidden="true"
                />
                {{ address.label }}
              </span>
            </span>
            <span
              v-if="entry.mode === 'own' && choice === 'own'"
              class="mt-2 grid gap-1.5"
            >
              <Input
                v-model="ownAddress"
                type="url"
                placeholder="https://fleet.example.com"
                aria-label="Your own address"
                data-testid="reach-own-address"
                :disabled="busy"
              />
              <small
                v-if="ownPort"
                class="text-xs text-muted"
              >Point your tunnel or proxy at <code>127.0.0.1:{{ ownPort }}</code>. That port always asks for a key.</small>
            </span>
          </span>
        </label>
      </div>

      <ErrorNote
        v-if="error"
        :message="error"
      />

      <DialogFooter class="sm:justify-between">
        <Button
          v-if="isOn"
          variant="outline"
          :disabled="busy"
          data-testid="reach-turn-off"
          @click="apply('off')"
        >
          Turn off
        </Button>
        <span
          v-else
          aria-hidden="true"
        />
        <span class="flex gap-2">
          <Button
            variant="outline"
            :disabled="busy"
            @click="emit('update:open', false)"
          >
            Cancel
          </Button>
          <Button
            :disabled="busy || !canTurnOn"
            data-testid="reach-turn-on"
            @click="apply(choice)"
          >
            <Spinner
              v-if="busy"
              size="sm"
            />
            {{ isOn ? "Save" : "Turn on" }}
          </Button>
        </span>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.reach-choice {
  display: flex;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  cursor: pointer;
  transition: border-color var(--transition), background-color var(--transition);
}

.reach-choice--on {
  border-color: var(--accent);
  background: var(--accent-dim, var(--card-bg));
}

.reach-choice--off {
  cursor: not-allowed;
  opacity: 0.6;
}

.reach-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border: 1px solid var(--border);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--text);
}
</style>
