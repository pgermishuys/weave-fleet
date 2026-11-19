<script setup lang="ts">
import { onMounted, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import FoundMachinesCard from "@/components/settings/FoundMachinesCard.vue";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { getDesktopBridge } from "@/lib/desktop";
import { useMachinesStore } from "@/stores/machines";

/**
 * The desktop app's first run: looks once for a Fleet that is waiting for it (a machine on Tailscale or the home network
 * that ran `fleet node pair`), joins it, then asks "Where should new sessions run?". Nothing waiting means nothing shows
 * and it doesn't look again; Add a machine → Found is always there later. Only in the desktop app's window.
 */
const DONE_KEY = "fleet.first-run-fleet.v1";

const machines = useMachinesStore();
const { foundMachines, entries } = storeToRefs(machines);

type Step = "closed" | "find" | "where";
const step = shallowRef<Step>("closed");
const joined = shallowRef<string | null>(null);

function isDone(): boolean {
  try {
    return localStorage.getItem(DONE_KEY) === "true";
  } catch {
    return true;
  }
}

function markDone(): void {
  try {
    localStorage.setItem(DONE_KEY, "true");
  } catch {
    // Can't remember: it may ask once more, which is the safe way to fail.
  }
}

onMounted(async () => {
  if (!getDesktopBridge() || isDone()) return;
  await machines.loadFound();
  if ((foundMachines.value?.length ?? 0) === 0) {
    markDone();
    return;
  }
  step.value = "find";
});

function onJoined(name: string): void {
  joined.value = name;
  step.value = "where";
}

function runHere(key: string): void {
  machines.setCurrent(key);
  finish();
}

function finish(): void {
  markDone();
  step.value = "closed";
}
</script>

<template>
  <Dialog
    :open="step !== 'closed'"
    @update:open="(open: boolean) => { if (!open) finish(); }"
  >
    <DialogContent data-testid="first-run-fleet">
      <template v-if="step === 'find'">
        <DialogHeader>
          <DialogTitle>Found your Fleet</DialogTitle>
          <DialogDescription>
            A machine is waiting to be added. Join it and this app sees every machine in your Fleet.
          </DialogDescription>
        </DialogHeader>
        <FoundMachinesCard
          @added="onJoined"
          @use-paste="finish"
        />
        <DialogFooter>
          <Button
            variant="ghost"
            data-testid="first-run-skip"
            @click="finish"
          >
            Not now
          </Button>
        </DialogFooter>
      </template>
      <template v-else-if="step === 'where'">
        <DialogHeader>
          <DialogTitle>Where should new sessions run?</DialogTitle>
          <DialogDescription>
            {{ joined }} joined your Fleet. Pick where new sessions start; you can change it for any session.
          </DialogDescription>
        </DialogHeader>
        <ul
          class="grid gap-2"
          aria-label="Machines"
        >
          <li
            v-for="entry in entries"
            :key="entry.key"
          >
            <Button
              variant="outline"
              class="w-full justify-start"
              data-testid="first-run-machine"
              @click="runHere(entry.key)"
            >
              {{ entry.isLive ? `This machine (${entry.name})` : entry.name }}
            </Button>
          </li>
        </ul>
      </template>
    </DialogContent>
  </Dialog>
</template>
