<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import PhoneNewSessionForm from "@/components/phone/new/PhoneNewSessionForm.vue";
import { readCredentialsSync } from "@/lib/device-credentials";
import { provideMachineTarget, targetFor } from "@/lib/machine-target";
import { fetchMachineList, type ListedMachine } from "@/lib/phone/grants";
import { phoneMachines, type PhoneMachine } from "@/lib/phone/new-session";

/**
 * `/phone/new`: start a session from the phone, on home or any machine the phone has its own key for. Everything the
 * form loads (folders, harnesses, agents and models) and the session it starts go to the chosen machine; changing
 * machine builds the form again for it.
 */
const props = defineProps<{ machineId?: string }>();

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

provideMachineTarget(() => targetFor(selected.value?.connection ?? null));

onMounted(async () => {
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
});
</script>

<template>
  <PhoneNewSessionForm
    v-if="selected"
    :key="selected.id"
    :machine="selected"
    :machines="machines"
    @machine="(id) => (selectedId = id)"
  />
</template>
