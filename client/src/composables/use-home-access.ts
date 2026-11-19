import { computed, shallowRef } from "vue";
import { useMachinesStore, type MachineAccess } from "@/stores/machines";
import { useMachineReach } from "@/composables/use-machine-reach";
import { listDevices, type PairedDevice } from "@/lib/devices-api";

/**
 * How other devices reach the machine this client is signed in to (its token, addresses, who may reach it) and the
 * phones and browsers that have keys on it. Owner only: a paired device sees none of it.
 */
export function useHomeAccess() {
  const machines = useMachinesStore();
  const reach = useMachineReach();
  const access = shallowRef<MachineAccess | null>(null);
  /** This browser signed in as a paired device: it may not see the token or manage devices. */
  const isDevice = shallowRef(false);
  const loaded = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const devices = shallowRef<PairedDevice[]>([]);
  /** Phones and browsers: every screen that has keys here. Another machine's key is not a screen. */
  const screens = computed(() => devices.value.filter((device) => device.kind !== "machine"));

  async function refreshDevices(): Promise<void> {
    try {
      devices.value = await listDevices();
    } catch {
      // An older Fleet has no devices; the list just stays empty.
    }
  }

  async function load(): Promise<void> {
    error.value = null;
    try {
      const result = await machines.loadHomeAccess();
      isDevice.value = result === "device";
      access.value = result === "device" ? null : result;
      if (access.value) {
        void refreshDevices();
        void reach.load();
      }
    } catch (failure) {
      error.value = failure instanceof Error ? failure.message : String(failure);
    } finally {
      loaded.value = true;
    }
  }

  return { access, isDevice, loaded, error, devices, screens, load, refreshDevices, reach };
}
