import { onMounted, onUnmounted, readonly, shallowRef } from "vue";
import { getDesktopBridge, type DesktopUpdateState } from "@/lib/desktop";

/** The desktop app's update state, live, when the UI runs in the app. */
export function useDesktopUpdates() {
  const bridge = getDesktopBridge();
  const state = shallowRef<DesktopUpdateState | null>(null);
  const isChecking = shallowRef(false);
  let stopListening: (() => void) | undefined;

  onMounted(async () => {
    if (!bridge) return;
    stopListening = bridge.onUpdateState((next) => {
      state.value = next;
    });
    state.value = await bridge.getUpdateState();
  });

  onUnmounted(() => stopListening?.());

  async function check(): Promise<void> {
    if (!bridge) return;
    isChecking.value = true;
    try {
      state.value = await bridge.checkForUpdates();
    } finally {
      isChecking.value = false;
    }
  }

  async function install(): Promise<void> {
    await bridge?.installUpdate();
  }

  return { inApp: bridge !== null, state: readonly(state), isChecking: readonly(isChecking), check, install };
}
