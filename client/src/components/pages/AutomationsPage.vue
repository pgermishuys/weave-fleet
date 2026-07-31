<script setup lang="ts">
import { shallowRef } from "vue";
import { LoaderCircle, Plus } from "lucide-vue-next";
import AutomationCard from "@/components/automations/AutomationCard.vue";
import AutomationForm from "@/components/automations/AutomationForm.vue";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { useAutomations, type Automation, type CreateAutomationRequest } from "@/composables/use-automations";

const {
  automations,
  isLoading,
  error,
  createAutomation,
  updateAutomation,
  deleteAutomation,
  enableAutomation,
  disableAutomation,
  runAutomation,
} = useAutomations();

const isDialogOpen = shallowRef(false);
const dialogMode = shallowRef<"create" | "edit">("create");
const editingAutomation = shallowRef<Automation | null>(null);
const isSubmitting = shallowRef(false);
const submitError = shallowRef<string | null>(null);

const deleteConfirmOpen = shallowRef(false);
const deletingAutomationId = shallowRef<string | null>(null);
const isDeleting = shallowRef(false);

function handleNewAutomation() {
  dialogMode.value = "create";
  editingAutomation.value = null;
  submitError.value = null;
  isDialogOpen.value = true;
}

function handleEdit(id: string) {
  const automation = automations.value.find((a) => a.id === id);
  if (!automation) return;

  dialogMode.value = "edit";
  editingAutomation.value = automation;
  submitError.value = null;
  isDialogOpen.value = true;
}

async function handlePlay(id: string) {
  const automation = automations.value.find((a) => a.id === id);
  if (!automation) return;

  try {
    if (automation.isEnabled) {
      await runAutomation(id);
    } else {
      await enableAutomation(id);
    }
  } catch (err) {
    console.error("Failed to play automation:", err);
  }
}

async function handlePause(id: string) {
  try {
    await disableAutomation(id);
  } catch (err) {
    console.error("Failed to pause automation:", err);
  }
}

function handleDelete(id: string) {
  deletingAutomationId.value = id;
  deleteConfirmOpen.value = true;
}

async function confirmDelete() {
  if (!deletingAutomationId.value) return;

  isDeleting.value = true;
  try {
    await deleteAutomation(deletingAutomationId.value);
    deleteConfirmOpen.value = false;
    deletingAutomationId.value = null;
  } catch (err) {
    console.error("Failed to delete automation:", err);
  } finally {
    isDeleting.value = false;
  }
}

function cancelDelete() {
  deleteConfirmOpen.value = false;
  deletingAutomationId.value = null;
}

async function handleFormSubmit(data: CreateAutomationRequest) {
  isSubmitting.value = true;
  submitError.value = null;

  try {
    if (dialogMode.value === "create") {
      await createAutomation(data);
    } else if (editingAutomation.value) {
      await updateAutomation(editingAutomation.value.id, data);
    }
    isDialogOpen.value = false;
    editingAutomation.value = null;
  } catch (err) {
    submitError.value = err instanceof Error ? err.message : "Failed to save automation";
  } finally {
    isSubmitting.value = false;
  }
}

function handleFormCancel() {
  isDialogOpen.value = false;
  editingAutomation.value = null;
  submitError.value = null;
}
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <header class="flex items-center justify-between">
      <h1 class="text-2xl font-semibold tracking-tight text-text">
        Automations
      </h1>
      <Button
        variant="default"
        size="default"
        @click="handleNewAutomation"
      >
        <Plus :size="16" />
        New Automation
      </Button>
    </header>

    <!-- Loading state -->
    <div
      v-if="isLoading"
      class="flex flex-1 items-center justify-center"
    >
      <LoaderCircle class="h-8 w-8 animate-spin text-muted" />
    </div>

    <!-- Error state -->
    <div
      v-else-if="error"
      class="flex flex-1 items-center justify-center text-sm text-destructive"
    >
      Failed to load automations: {{ error }}
    </div>

    <!-- Empty state -->
    <div
      v-else-if="automations.length === 0"
      class="flex flex-1 flex-col items-center justify-center gap-4 text-center"
    >
      <p class="text-sm text-muted">
        No automations configured yet.
      </p>
      <Button
        variant="default"
        size="default"
        @click="handleNewAutomation"
      >
        <Plus :size="16" />
        Create Your First Automation
      </Button>
    </div>

    <!-- Automations list -->
    <section
      v-else
      class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    >
      <div class="flex flex-col gap-1">
        <h2 class="text-lg font-semibold text-text">Configured Automations</h2>
        <p class="text-sm text-muted">Manage scheduled and event-triggered automations</p>
      </div>
      <div class="mt-5 grid gap-3">
        <AutomationCard
          v-for="automation in automations"
          :key="automation.id"
          :automation="automation"
          @play="handlePlay"
          @pause="handlePause"
          @edit="handleEdit"
          @delete="handleDelete"
        />
      </div>
    </section>

    <!-- Create/Edit Dialog -->
    <Dialog
      v-model:open="isDialogOpen"
    >
      <DialogContent class="max-h-[90vh] overflow-y-auto sm:max-w-[600px]">
        <DialogHeader>
          <DialogTitle>
            {{ dialogMode === "create" ? "Create Automation" : "Edit Automation" }}
          </DialogTitle>
        </DialogHeader>

        <AutomationForm
          :mode="dialogMode"
          :initial-values="editingAutomation ?? undefined"
          @submit="handleFormSubmit"
          @cancel="handleFormCancel"
        />

        <div
          v-if="submitError"
          class="mt-4 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
        >
          {{ submitError }}
        </div>
      </DialogContent>
    </Dialog>

    <!-- Delete Confirmation Dialog -->
    <AlertDialog v-model:open="deleteConfirmOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete Automation</AlertDialogTitle>
          <AlertDialogDescription>
            Are you sure you want to delete this automation? This action cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel
            :disabled="isDeleting"
            @click="cancelDelete"
          >
            Cancel
          </AlertDialogCancel>
          <AlertDialogAction
            :disabled="isDeleting"
            @click="confirmDelete"
          >
            <LoaderCircle
              v-if="isDeleting"
              class="mr-2 h-4 w-4 animate-spin"
            />
            Delete
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </section>
</template>
