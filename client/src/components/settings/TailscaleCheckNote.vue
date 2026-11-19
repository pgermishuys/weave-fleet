<script setup lang="ts">
import { shallowRef } from "vue";
import { CircleAlert, CircleCheck } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";
import { useMachineReach } from "@/composables/use-machine-reach";
import type { TailscaleCheck } from "@/lib/reach-api";

/**
 * What's wrong with Tailscale on this machine, in one line, with the one button that fixes what Fleet can fix:
 * "Turn on" opens the listener on the Tailscale address (and looks for the address again, so a Tailscale that came
 * up after Fleet started is found). When Tailscale itself is missing or off Fleet can't do that for you: it says so
 * and "Check again" reads it afresh.
 */
defineProps<{ check: TailscaleCheck }>();

const emit = defineEmits<{ changed: [] }>();

const reach = useMachineReach();
const busy = shallowRef(false);
const error = shallowRef<string | null>(null);

async function run(action: () => Promise<void>): Promise<void> {
  busy.value = true;
  error.value = null;
  try {
    await action();
    emit("changed");
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div
    class="grid gap-2 rounded-card border border-border bg-card-bg px-3 py-2.5"
    data-testid="tailscale-check"
    :data-state="check.state"
  >
    <div class="flex items-start gap-2">
      <CircleCheck
        v-if="check.covered"
        :size="16"
        class="mt-0.5 shrink-0 text-running-text"
        aria-hidden="true"
      />
      <CircleAlert
        v-else
        :size="16"
        class="mt-0.5 shrink-0 text-muted"
        aria-hidden="true"
      />
      <p class="min-w-0 flex-1 text-sm">
        <b class="text-text">{{ check.headline }}</b>
        <span class="mt-0.5 block text-xs text-muted">{{ check.detail }}</span>
      </p>
      <Button
        v-if="check.canTurnOn"
        size="sm"
        :disabled="busy"
        data-testid="tailscale-turn-on"
        @click="run(() => reach.set('tailscale-and-home'))"
      >
        <Spinner
          v-if="busy"
          size="sm"
        />
        Turn on
      </Button>
      <Button
        v-else-if="!check.covered"
        size="sm"
        variant="outline"
        :disabled="busy"
        data-testid="tailscale-check-again"
        @click="run(() => reach.load())"
      >
        Check again
      </Button>
    </div>
    <ErrorNote
      v-if="error"
      compact
      :message="error"
    />
  </div>
</template>
