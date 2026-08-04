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
const targetTags = shallowRef<string[]>([]);
const targetType = shallowRef<string>("new_session");
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

const isDirty = computed(() => {
  if (props.mode === "create") {
    return true; // Always allow submit in create mode if validation passes
  }

  if (!props.initialValues) {
    return false;
  }

  // Check if any field has changed from initial values
  const initial = props.initialValues;
  
  if (name.value !== (initial.name ?? "")) return true;
  if (prompt.value !== (initial.prompt ?? "")) return true;
  if (triggerType.value !== (initial.triggerType === "event" ? "event" : "schedule")) return true;
  
  // Compare triggerConfig, extracting eventType from JSON if needed
  let initialConfigValue = initial.triggerConfig ?? "";
  if (initial.triggerType === "event" && initialConfigValue) {
    try {
      const parsed = JSON.parse(initialConfigValue);
      if (parsed.eventType) {
        initialConfigValue = parsed.eventType;
      }
    } catch {
      // If parsing fails, use the raw value
    }
  }
  if (triggerConfig.value !== initialConfigValue) return true;
  
  if (maxConcurrentRuns.value !== (initial.maxConcurrentRuns ?? 1)) return true;
  if (maxRunsPerHour.value !== (initial.maxRunsPerHour ?? 10)) return true;
  if (timeoutMinutes.value !== (initial.timeoutMinutes ?? 30)) return true;
  if (workspaceId.value !== (initial.workspaceId ?? "")) return true;
  if (model.value !== (initial.model ?? "")) return true;
  if (agent.value !== (initial.agent ?? "")) return true;
  if (targetType.value !== (initial.targetType ?? "new_session")) return true;
  
  // Check target tags array
  const initialTags = initial.targetTags ?? [];
  if (targetTags.value.length !== initialTags.length) return true;
  if (targetTags.value.some((tag, idx) => tag !== initialTags[idx])) return true;

  return false;
});

const dialogError = computed(() => {
  if (submitAttempted.value && validationMessage.value) {
    return validationMessage.value;
  }

  return eventCatalogError.value;
});

const canSubmit = computed(() => {
  return validationMessage.value === null && isDirty.value;
});

function initializeForm(): void {
  if (props.initialValues) {
    name.value = props.initialValues.name ?? "";
    prompt.value = props.initialValues.prompt ?? "";
    triggerType.value = (props.initialValues.triggerType === "event" ? "event" : "schedule") as "schedule" | "event";
    
    // Extract eventType from JSON if this is an event trigger
    let configValue = props.initialValues.triggerConfig ?? "";
    if (props.initialValues.triggerType === "event" && configValue) {
      try {
        const parsed = JSON.parse(configValue);
        if (parsed.eventType) {
          configValue = parsed.eventType;
        }
      } catch {
        // If parsing fails, use the raw value
      }
    }
    triggerConfig.value = configValue;
    
    maxConcurrentRuns.value = props.initialValues.maxConcurrentRuns ?? 1;
    maxRunsPerHour.value = props.initialValues.maxRunsPerHour ?? 10;
    timeoutMinutes.value = props.initialValues.timeoutMinutes ?? 30;
    workspaceId.value = props.initialValues.workspaceId ?? "";
    model.value = props.initialValues.model ?? "";
    agent.value = props.initialValues.agent ?? "";
    targetTags.value = props.initialValues.targetTags ? [...props.initialValues.targetTags] : [];
    targetType.value = props.initialValues.targetType ?? "new_session";
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
  targetTags.value = [];
  targetType.value = "new_session";
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

  // Wrap event type in JSON for event triggers
  const configValue = triggerType.value === "event"
    ? JSON.stringify({ eventType: trimmedTriggerConfig.value })
    : trimmedTriggerConfig.value;

  const data: CreateAutomationRequest = {
    name: trimmedName.value,
    prompt: trimmedPrompt.value,
    triggerType: triggerType.value,
    triggerConfig: configValue,
    maxConcurrentRuns: maxConcurrentRuns.value,
    maxRunsPerHour: maxRunsPerHour.value,
    timeoutMinutes: timeoutMinutes.value,
    workspaceId: workspaceId.value.trim() || null,
    model: model.value.trim() || null,
    agent: agent.value.trim() || null,
    targetType: targetType.value,
    targetTags: targetTags.value.length > 0 ? targetTags.value : undefined,
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

    <div class="space-y-2">
      <label
        for="automation-target-type"
        class="text-sm font-medium text-foreground"
      >Target Type</label>
      <Select
        id="automation-target-type"
        v-model="targetType"
      >
        <SelectTrigger class="w-full">
          <SelectValue placeholder="Select target type…" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="new_session">
            New Session
          </SelectItem>
          <SelectItem value="most_recent_session">
            Most Recent Session
          </SelectItem>
          <SelectItem value="tagged_session">
            Tagged Session
          </SelectItem>
        </SelectContent>
      </Select>
    </div>

    <div
      v-if="targetType === 'tagged_session'"
      class="space-y-2"
    >
      <label
        for="automation-target-tags"
        class="text-sm font-medium text-foreground"
      >Target Tags <span class="font-normal text-muted-foreground">(optional)</span></label>
      <Input
        id="automation-target-tags"
        :model-value="targetTags.join(', ')"
        placeholder="tag1, tag2, tag3"
        @update:model-value="(value: string | number) => targetTags = String(value).split(',').map((t: string) => t.trim()).filter((t: string) => t.length > 0)"
      />
      <p class="text-xs text-muted-foreground opacity-50">
        Sessions must have at least one matching tag
      </p>
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
