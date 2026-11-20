<script setup lang="ts">
import { shallowRef } from "vue";
import { Button } from "@/components/ui/button";
import { removeDevice, removeFleetScreen, type PairedDevice } from "@/lib/devices-api";
import { formatDate, formatDateTime, formatRelative } from "@/lib/format";
import { ErrorNote } from "@/components/ui/error-note";
import { confirmAction } from "@/components/ui/confirm";

/**
 * Settings → Machines → This machine → Devices with access: this computer, then each paired device with when it
 * was added and last seen, and Remove. Removing one signs out only that device.
 */
defineProps<{
  devices: readonly PairedDevice[];
  /** How this computer is signed in, for its row. */
  thisComputer: string;
  /** Phones and browsers only: leaves out the row for the machine itself. */
  hideThisMachine?: boolean;
  /** Your Fleet: machines that didn't answer, so what they hold isn't in the list. */
  notAnswering?: readonly string[];
}>();

const emit = defineEmits<{ (event: "removed", id: string): void }>();

const busy = shallowRef<string | null>(null);
const error = shallowRef<string | null>(null);

function added(device: PairedDevice): string {
  const date = formatDate(device.createdAt);
  if (device.kind === "machine") return `Machine, paired ${date}`;
  // The Fleet-wide list says which machine it connected through.
  if (device.fleetWide) return device.throughName ? `Connected through ${device.throughName}, added ${date}` : `Added ${date}`;
  return device.pairedVia ? `Added ${date} from another machine` : `Added ${date}`;
}

function lastSeen(device: PairedDevice): string {
  return formatRelative(device.lastUsedAt);
}

async function remove(device: PairedDevice): Promise<void> {
  // A phone or a browser (not another machine's key) goes from every machine (Your Fleet).
  const reachesAll = device.kind !== "machine";
  const confirmed = await confirmAction(reachesAll
    ? {
        title: `Remove ${device.name} on every machine in your Fleet?`,
        body: "It loses its keys on every machine at once, even on machines that are off now: they hear when they're back. It isn't given new ones.",
        confirmLabel: `Remove ${device.name}`,
        destructive: true,
      }
    : {
        title: `Remove ${device.name}?`,
        body: "It loses access to this machine at once. Other devices aren't affected.",
        confirmLabel: device.kind === "machine" ? "Remove machine" : "Remove device",
        destructive: true,
      });
  if (!confirmed) return;
  busy.value = device.id;
  error.value = null;
  try {
    await (device.fleetWide ? removeFleetScreen(device.id) : removeDevice(device.id));
    emit("removed", device.id);
  } catch (failure) {
    error.value = failure instanceof Error ? failure.message : String(failure);
  } finally {
    busy.value = null;
  }
}

</script>

<template>
  <div
    class="mt-5 grid gap-2"
    data-testid="devices-with-access"
  >
    <span
      v-if="!hideThisMachine"
      class="text-xs font-medium uppercase tracking-wide text-muted"
    >Devices with access</span>
    <ul class="devices">
      <li
        v-if="!hideThisMachine"
        class="devices__row"
      >
        <div class="min-w-0 flex-1">
          <div class="text-sm text-text">
            This machine
          </div>
          <div class="text-xs text-muted">
            {{ thisComputer }}
          </div>
        </div>
      </li>
      <li
        v-for="device in devices"
        :key="device.id"
        class="devices__row"
        data-testid="device-row"
      >
        <div class="min-w-0 flex-1">
          <div class="truncate text-sm text-text">
            {{ device.name }}
          </div>
          <div
            class="text-xs text-muted"
            :title="`Added ${formatDateTime(device.createdAt)}, last seen ${formatDateTime(device.lastUsedAt)}`"
          >
            {{ added(device) }} · last seen {{ lastSeen(device) }}
          </div>
        </div>
        <Button
          variant="outline"
          size="sm"
          :disabled="busy === device.id"
          :data-testid="`remove-device-${device.id}`"
          @click="remove(device)"
        >
          Remove
        </Button>
      </li>
    </ul>
    <p
      v-if="notAnswering?.length"
      class="text-xs text-muted"
      data-testid="devices-not-answering"
    >
      {{ notAnswering.join(", ") }} {{ notAnswering.length === 1 ? "isn't" : "aren't" }} answering, so what {{ notAnswering.length === 1 ? "it holds" : "they hold" }} isn't shown.
    </p>
    <ErrorNote
      v-if="error"
      :message="error"
    />
  </div>
</template>

<style scoped>
.devices {
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.devices__row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 52px;
  padding: 8px 14px;
}

.devices__row + .devices__row {
  border-top: 1px solid var(--border);
}
</style>
