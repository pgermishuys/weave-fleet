import { onMounted, onUnmounted, readonly, shallowRef, type ShallowRef } from "vue";

const TICK_INTERVAL_MS = 30_000;

const now = shallowRef(Date.now());
let consumerCount = 0;
let timer: ReturnType<typeof setInterval> | null = null;

function startTimer(): void {
  if (timer !== null) {
    return;
  }

  timer = setInterval(() => {
    now.value = Date.now();
  }, TICK_INTERVAL_MS);
}

function stopTimer(): void {
  if (timer === null) {
    return;
  }

  clearInterval(timer);
  timer = null;
}

export function useRelativeTime(): Readonly<ShallowRef<number>> {
  onMounted(() => {
    consumerCount += 1;
    startTimer();
  });

  onUnmounted(() => {
    consumerCount = Math.max(0, consumerCount - 1);
    if (consumerCount === 0) {
      stopTimer();
    }
  });

  return readonly(now);
}
