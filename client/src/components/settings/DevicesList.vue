<script setup lang="ts">
import { shallowRef } from "vue";
import { Button } from "@/components/ui/button";
import { removeDevice, type PairedDevice } from "@/lib/devices-api";
import { formatRelativeTime } from "@/lib/format-utils";

/**
 * Settings → Machines → This machine → Devices with access: this computer, then each paired device with when it
 * was added and last seen, and Remove. Removing one signs out only that device.
 */
defineProps<{
  devices: readonly PairedDevice[];
  /** How this computer is signed in, for its row. */
  thisComputer: string;
}>();

const emit = defineEmits<{ (event: "removed", id: string): void }>();

const busy = shallowRef<string | null>(null);
const error = shallowRef<string | null>(null);

function added(device: PairedDevice): string {
  const date = new Date(device.createdAt).toLocaleDateString(undefined, { day: "numeric", month: "short" });
  return device.pairedVia ? `Added ${date} from another machine` : `Added ${date}`;
}

function lastSeen(device: PairedDevice): string {
  const days = Math.floor((Date.now() - Date.parse(device.lastUsedAt)) / 86_400_000);
  if (days < 1) return formatRelativeTime(device.lastUsedAt).replace(/m ago$/, " min ago");
  return days === 1 ? "yesterday" : `${days} days ago`;
}

async function remove(device: PairedDevice): Promise<void> {
  if (!window.confirm(`Remove ${device.name}? It loses access to this machine at once. Other devices aren't affected.`)) return;
  busy.value = device.id;
  error.value = null;
  try {
    await removeDevice(device.id);
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
    <span class="text-xs font-medium uppercase tracking-wide text-muted">Devices with access</span>
    <ul class="devices">
      <li class="devices__row">
        <div class="min-w-0 flex-1">
          <div class="text-sm text-text">
            This computer
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
          <div class="text-xs text-muted">
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
      v-if="error"
      class="text-sm text-error"
      role="alert"
    >
      {{ error }}
    </p>
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
