<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PhoneNewSessionForm from "@/components/phone/new/PhoneNewSessionForm.vue";
import { usePhoneNav } from "@/composables/phone/use-phone-nav";
import { readCredentialsSync } from "@/lib/device-credentials";
import { provideMachineTarget, targetFor } from "@/lib/machine-target";
import { rememberPhoneMachine } from "@/lib/machines";
import { fetchMachineList, type ListedMachine } from "@/lib/phone/grants";
import { phoneMachines, type PhoneMachine } from "@/lib/phone/new-session";

/**
 * New session (`/phone/new`): a sheet over the inbox that brings the keyboard up, to start a session on home or any
 * machine the phone has its own key for. Everything the form loads (folders, harnesses, agents and models) and the
 * session it starts go to the chosen machine; changing machine builds the form again for it, keeping what's typed.
 */
const props = defineProps<{ open: boolean; machineId?: string }>();
const emit = defineEmits<{ (event: "close"): void }>();
const nav = usePhoneNav();

const credentials = readCredentialsSync();
const listed = shallowRef<ListedMachine[]>([]);
// A browser signed in without pairing (the owner's own phone) has no stored credentials: home is all it gets.
const fallbackHome = shallowRef<PhoneMachine | null>(null);
const machines = computed<PhoneMachine[]>(() => {
  const paired = phoneMachines(credentials, listed.value);
  return paired.length ? paired : fallbackHome.value ? [fallbackHome.value] : [];
});
const selectedId = shallowRef(props.machineId ?? credentials?.homeMachineId ?? "");
const selected = computed(() => machines.value.find((machine) => machine.id === selectedId.value) ?? machines.value[0] ?? null);
const message = shallowRef("");

provideMachineTarget(() => targetFor(selected.value?.connection ?? null));

let loaded = false;
async function load(): Promise<void> {
  if (loaded) return;
  loaded = true;
  if (credentials) {
    listed.value = await fetchMachineList(credentials.token);
    return;
  }
  try {
    const response = await fetch("/api/machine", { credentials: "include" });
    if (!response.ok) return;
    const machine = await response.json() as { id: string; name: string };
    fallbackHome.value = { id: machine.id, name: machine.name, connection: null };
  } catch {
    // Nothing to offer; the form says so.
  }
}

watch(() => props.open, (open) => {
  if (!open) return;
  if (props.machineId) selectedId.value = props.machineId;
  void load();
}, { immediate: true });

function started(sessionId: string): void {
  const machine = selected.value;
  message.value = "";
  if (!machine?.connection) {
    // The session takes the sheet's place in history, so Back from it comes to the inbox.
    void nav.openSession(machine?.id ?? "", sessionId, { replace: true });
    return;
  }
  // Another machine: the page reloads to work there, as opening one from the inbox does.
  rememberPhoneMachine(machine.connection);
  window.location.assign(`/phone/s/${encodeURIComponent(machine.id)}/${encodeURIComponent(sessionId)}`);
}
</script>

<template>
  <BottomSheet
    :open="open"
    label="New session"
    :detents="['large']"
    bare
    :history="false"
    @close="emit('close')"
  >
    <div class="ph-sheet-pages">
      <PhoneNewSessionForm
        v-if="selected"
        :key="selected.id"
        v-model:message="message"
        :machine="selected"
        :machines="machines"
        @machine="(id) => (selectedId = id)"
        @cancel="emit('close')"
        @started="started"
      />
    </div>
  </BottomSheet>
</template>
