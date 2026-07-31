<script setup lang="ts">
import { computed, nextTick, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { Automation, CreateAutomationRequest } from "@/composables/use-automations";
import { useAutomations } from "@/composables/use-automations";

interface Props {
  initialValues?: Partial<Automation>;
  mode: "create" | "edit";
}

const props = defineProps<Props>();

const emit = defineEmits<{
  submit: [data: CreateAutomationRequest];
  cancel: [];
}>();

const name = shallowRef("");
const prompt = shallowRef("");
const triggerType = shallowRef<"schedule" | "event">("schedule");
const triggerConfig = shallowRef("");
const maxConcurrentRuns = shallowRef(1);
const maxRunsPerHour = shallowRef(10);
const timeoutMinutes = shallowRef(30);
const workspaceId = shallowRef<string>("");
const model = shallowRef<string>("");
const agent = shallowRef<string>("");
const submitAttempted = shallowRef(false);
const isLoadingEventCatalog = shallowRef(false);
const eventCatalog = shallowRef<string[]>([]);
const eventCatalogError = shallowRef<string | null>(null);

const { fetchEventCatalog } = useAutomations();

const trimmedName = computed(() => name.value.trim());
const trimmedPrompt = computed(() => prompt.value.trim());
const trimmedTriggerConfig = computed(() => triggerConfig.value.trim());

const validationMessage = computed(() => {
  if (!trimmedName.value) {
    return "Name is required.";
  }

  if (!trimmedPrompt.value) {
    return "Prompt is required.";
  }

  if (!trimmedTriggerConfig.value) {
    return triggerType.value === "schedule"
      ? "Cron expression is required."
      : "Event type is required.";
  }

  if (maxConcurrentRuns.value < 1) {
    return "Max concurrent runs must be at least 1.";
  }

  if (maxRunsPerHour.value < 1) {
    return "Max runs per hour must be at least 1.";
  }

  if (timeoutMinutes.value < 1) {
    return "Timeout must be at least 1 minute.";
  }

  return null;
});

const dialogError = computed(() => {
  if (submitAttempted.value && validationMessage.value) {
    return validationMessage.value;
  }

  return eventCatalogError.value;
});

const canSubmit = computed(() => {
  return validationMessage.value === null;
});

function initializeForm(): void {
  if (props.initialValues) {
    name.value = props.initialValues.name ?? "";
    prompt.value = props.initialValues.prompt ?? "";
    triggerType.value = (props.initialValues.triggerType === "event" ? "event" : "schedule") as "schedule" | "event";
    triggerConfig.value = props.initialValues.triggerConfig ?? "";
    maxConcurrentRuns.value = props.initialValues.maxConcurrentRuns ?? 1;
    maxRunsPerHour.value = props.initialValues.maxRunsPerHour ?? 10;
    timeoutMinutes.value = props.initialValues.timeoutMinutes ?? 30;
    workspaceId.value = props.initialValues.workspaceId ?? "";
    model.value = props.initialValues.model ?? "";
    agent.value = props.initialValues.agent ?? "";
  } else {
    resetForm();
  }
}

function resetForm(): void {
  name.value = "";
  prompt.value = "";
  triggerType.value = "schedule";
  triggerConfig.value = "";
  maxConcurrentRuns.value = 1;
  maxRunsPerHour.value = 10;
  timeoutMinutes.value = 30;
  workspaceId.value = "";
  model.value = "";
  agent.value = "";
  submitAttempted.value = false;
  eventCatalogError.value = null;
}

async function loadEventCatalog(): Promise<void> {
  if (eventCatalog.value.length > 0) {
    return;
  }

  isLoadingEventCatalog.value = true;
  eventCatalogError.value = null;

  try {
    const catalog = await fetchEventCatalog();
    eventCatalog.value = catalog;
  } catch (error) {
    eventCatalogError.value = error instanceof Error ? error.message : "Failed to load event catalog";
  } finally {
    isLoadingEventCatalog.value = false;
  }
}

function handleSubmit(): void {
  submitAttempted.value = true;

  if (!canSubmit.value) {
    return;
  }

  const data: CreateAutomationRequest = {
    name: trimmedName.value,
    prompt: trimmedPrompt.value,
    triggerType: triggerType.value,
    triggerConfig: trimmedTriggerConfig.value,
    maxConcurrentRuns: maxConcurrentRuns.value,
    maxRunsPerHour: maxRunsPerHour.value,
    timeoutMinutes: timeoutMinutes.value,
    workspaceId: workspaceId.value.trim() || null,
    model: model.value.trim() || null,
    agent: agent.value.trim() || null,
  };

  emit("submit", data);
}

function handleCancel(): void {
  emit("cancel");
}

watch(triggerType, async (newType) => {
  // Clear trigger config when switching types
  triggerConfig.value = "";

  // Load event catalog when switching to event type
  if (newType === "event") {
    await loadEventCatalog();
  }
});

// Initialize form when component mounts or initialValues change
watch(
  () => props.initialValues,
  () => {
    initializeForm();
  },
  { immediate: true },
);

// Load event catalog if initial trigger type is event
watch(
  () => props.initialValues?.triggerType,
  async (type) => {
    if (type === "event") {
      await loadEventCatalog();
    }
  },
  { immediate: true },
);
</script>

<template>
  <form
    class="space-y-5"
    @submit.prevent="handleSubmit"
  >
    <div class="space-y-2">
      <label
        for="automation-name"
        class="text-sm font-medium text-foreground"
      >Name</label>
      <Input
        id="automation-name"
        v-model="name"
        placeholder="Automation name"
        autofocus
      />
    </div>

    <div class="space-y-2">
      <label
        for="automation-prompt"
        class="text-sm font-medium text-foreground"
      >Prompt</label>
      <Textarea
        id="automation-prompt"
        v-model="prompt"
        placeholder="What should this automation do?"
        class="min-h-24"
      />
    </div>

    <div class="space-y-2">
      <span class="text-sm font-medium text-foreground">Trigger Type</span>

      <div
        class="flex gap-3"
        role="radiogroup"
        aria-label="Trigger Type"
      >
        <button
          type="button"
          role="radio"
          :aria-checked="triggerType === 'schedule'"
          :tabindex="triggerType === 'schedule' ? 0 : -1"
          :class="[
            'inline-flex flex-1 items-center justify-center gap-2 border px-4 py-2 text-xs font-medium transition-colors',
            triggerType === 'schedule'
              ? 'border-primary bg-primary/10 text-primary'
              : 'border-border text-muted-foreground hover:text-foreground',
          ]"
          @click="triggerType = 'schedule'"
        >
          Schedule
        </button>

        <button
          type="button"
          role="radio"
          :aria-checked="triggerType === 'event'"
          :tabindex="triggerType === 'event' ? 0 : -1"
          :class="[
            'inline-flex flex-1 items-center justify-center gap-2 border px-4 py-2 text-xs font-medium transition-colors',
            triggerType === 'event'
              ? 'border-primary bg-primary/10 text-primary'
              : 'border-border text-muted-foreground hover:text-foreground',
          ]"
          @click="triggerType = 'event'"
        >
          Event
        </button>
      </div>
    </div>

    <div
      v-if="triggerType === 'schedule'"
      class="space-y-2"
    >
      <label
        for="automation-trigger-config"
        class="text-sm font-medium text-foreground"
      >Cron Expression</label>
      <Input
        id="automation-trigger-config"
        v-model="triggerConfig"
        placeholder="0 0 * * *"
      />
      <p class="text-xs text-muted-foreground opacity-50">
        Standard cron format (minute hour day month weekday)
      </p>
    </div>

    <div
      v-else-if="triggerType === 'event'"
      class="space-y-2"
    >
      <label
        for="automation-event-type"
        class="text-sm font-medium text-foreground"
      >Event Type</label>

      <div
        v-if="isLoadingEventCatalog"
        class="flex items-center gap-2 text-xs text-muted-foreground"
      >
        <LoaderCircle class="h-3.5 w-3.5 animate-spin" />
        Loading event types…
      </div>

      <Select
        v-else-if="eventCatalog.length > 0"
        id="automation-event-type"
        v-model="triggerConfig"
      >
        <SelectTrigger class="w-full">
          <SelectValue placeholder="Select an event type…" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem
            v-for="eventType in eventCatalog"
            :key="eventType"
            :value="eventType"
          >
            {{ eventType }}
          </SelectItem>
        </SelectContent>
      </Select>

      <Input
        v-else
        id="automation-event-type"
        v-model="triggerConfig"
        placeholder="Event type"
      />
    </div>

    <div class="grid grid-cols-3 gap-3">
      <div class="space-y-2">
        <label
          for="automation-max-concurrent"
          class="text-sm font-medium text-foreground"
        >Max Concurrent</label>
        <Input
          id="automation-max-concurrent"
          v-model.number="maxConcurrentRuns"
          type="number"
          min="1"
        />
      </div>

      <div class="space-y-2">
        <label
          for="automation-max-per-hour"
          class="text-sm font-medium text-foreground"
        >Max Per Hour</label>
        <Input
          id="automation-max-per-hour"
          v-model.number="maxRunsPerHour"
          type="number"
          min="1"
        />
      </div>

      <div class="space-y-2">
        <label
          for="automation-timeout"
          class="text-sm font-medium text-foreground"
        >Timeout (min)</label>
        <Input
          id="automation-timeout"
          v-model.number="timeoutMinutes"
          type="number"
          min="1"
        />
      </div>
    </div>

    <div class="space-y-2">
      <label
        for="automation-workspace"
        class="text-sm font-medium text-foreground"
      >Workspace ID <span class="font-normal text-muted-foreground">(optional)</span></label>
      <Input
        id="automation-workspace"
        v-model="workspaceId"
        placeholder="Workspace ID"
      />
    </div>

    <div class="space-y-2">
      <label
        for="automation-model"
        class="text-sm font-medium text-foreground"
      >Model <span class="font-normal text-muted-foreground">(optional)</span></label>
      <Input
        id="automation-model"
        v-model="model"
        placeholder="Model name"
      />
    </div>

    <div class="space-y-2">
      <label
        for="automation-agent"
        class="text-sm font-medium text-foreground"
      >Agent <span class="font-normal text-muted-foreground">(optional)</span></label>
      <Input
        id="automation-agent"
        v-model="agent"
        placeholder="Agent name"
      />
    </div>

    <div
      v-if="dialogError"
      class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
      role="alert"
    >
      <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
      <p>{{ dialogError }}</p>
    </div>

    <div class="flex justify-end gap-3">
      <Button
        type="button"
        variant="outline"
        @click="handleCancel"
      >
        Cancel
      </Button>

      <Button
        type="submit"
        :disabled="!canSubmit"
      >
        {{ mode === "create" ? "Create Automation" : "Save Changes" }}
      </Button>
    </div>
  </form>
</template>
