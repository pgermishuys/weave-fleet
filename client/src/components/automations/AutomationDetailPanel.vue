<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useAutomationsNav } from '@/composables/use-automations-nav'
import { useAutomations } from '@/composables/use-automations'
import AutomationForm from '@/components/automations/AutomationForm.vue'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Switch } from '@/components/ui/switch'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { AlertCircle, Play, Edit, Trash2 } from 'lucide-vue-next'
import type { CreateAutomationRequest } from '@/composables/use-automations'
import { describeEventType, eventTypeOf, scheduleTimeZone } from '@/lib/automations'

const { viewMode, activeAutomationId, setActiveAutomation, clearSelection } = useAutomationsNav()
const {
  automations,
  createAutomation,
  updateAutomation,
  deleteAutomation,
  enableAutomation,
  disableAutomation,
  runAutomation,
} = useAutomations()

const isEditingInline = ref(false)
const deleteConfirmOpen = ref(false)
const automationToDelete = ref<string | null>(null)
const isTogglingEnabled = ref(false)
/** Why the server refused the last Create or Save; the form shows it. */
const formError = ref<string | null>(null)
/** Why the switch, Run now or Delete failed, or that a run started. */
const actionMessage = ref<{ kind: 'error' | 'info'; text: string } | null>(null)
let actionMessageTimer: ReturnType<typeof setTimeout> | undefined

function messageOf(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback
}

function showAction(kind: 'error' | 'info', text: string) {
  clearTimeout(actionMessageTimer)
  actionMessage.value = { kind, text }
  if (kind === 'info') {
    actionMessageTimer = setTimeout(() => { actionMessage.value = null }, 4000)
  }
}

// A message belongs to the automation it was about.
watch([activeAutomationId, viewMode], () => {
  formError.value = null
  actionMessage.value = null
  isEditingInline.value = false
})

const currentAutomation = computed(() => {
  if (!activeAutomationId.value) return null
  return automations.value.find(a => a.id === activeAutomationId.value) || null
})

async function handleCreate(data: CreateAutomationRequest) {
  formError.value = null
  try {
    const newAutomation = await createAutomation(data)
    setActiveAutomation(newAutomation.id)
  } catch (e) {
    formError.value = messageOf(e, 'Couldn\'t create the automation.')
  }
}

function handleCancelCreate() {
  formError.value = null
  clearSelection()
}

async function handleUpdate(data: CreateAutomationRequest) {
  if (!currentAutomation.value) return
  formError.value = null
  try {
    await updateAutomation(currentAutomation.value.id, data)
    isEditingInline.value = false
  } catch (e) {
    formError.value = messageOf(e, 'Couldn\'t save the automation.')
  }
}

function handleCancelEdit() {
  formError.value = null
  isEditingInline.value = false
}

function startEdit() {
  formError.value = null
  actionMessage.value = null
  isEditingInline.value = true
}

function startDelete(id: string) {
  automationToDelete.value = id
  deleteConfirmOpen.value = true
}

async function confirmDelete() {
  if (!automationToDelete.value) return
  try {
    await deleteAutomation(automationToDelete.value)
    clearSelection()
  } catch (e) {
    showAction('error', messageOf(e, 'Couldn\'t delete the automation.'))
  } finally {
    deleteConfirmOpen.value = false
    automationToDelete.value = null
  }
}

async function handlePlay() {
  if (!currentAutomation.value) return
  try {
    await runAutomation(currentAutomation.value.id)
    showAction('info', 'Run started. It shows up in Sessions.')
  } catch (e) {
    showAction('error', messageOf(e, 'Couldn\'t start a run.'))
  }
}

async function handleToggleEnabled(enabled: boolean) {
  if (!currentAutomation.value || isTogglingEnabled.value) return
  isTogglingEnabled.value = true
  try {
    if (enabled) {
      await enableAutomation(currentAutomation.value.id)
    } else {
      await disableAutomation(currentAutomation.value.id)
    }
  } catch (e) {
    showAction('error', messageOf(e, enabled ? 'Couldn\'t switch it on.' : 'Couldn\'t switch it off.'))
  } finally {
    isTogglingEnabled.value = false
  }
}

/** When it runs, in words: the cron and its zone, or the event it waits for. */
const triggerSummary = computed(() => {
  const automation = currentAutomation.value
  if (!automation) return ''
  if (automation.triggerType === 'event') return describeEventType(eventTypeOf(automation.triggerConfig))
  if (automation.triggerType === 'schedule') return `${automation.triggerConfig} (${scheduleTimeZone(automation.timeZone)})`
  return automation.triggerConfig || 'None'
})

function formatTargetType(targetType: string | undefined): string {
  if (!targetType) return 'New Session'
  switch (targetType) {
    case 'new_session':
      return 'New Session'
    case 'most_recent_session':
      return 'Most Recent Session'
    case 'tagged_session':
      return 'Tagged Session'
    default:
      return targetType
  }
}
</script>

<template>
  <div class="flex h-full flex-col">
    <!-- Empty state -->
    <div
      v-if="viewMode === 'list'"
      class="flex h-full items-center justify-center text-muted-foreground"
    >
      <p>Select an automation or create a new one</p>
    </div>

    <!-- Create mode -->
    <div
      v-else-if="viewMode === 'create'"
      class="flex-1 overflow-y-auto p-6"
    >
      <AutomationForm
        mode="create"
        :submit-error="formError"
        @submit="handleCreate"
        @cancel="handleCancelCreate"
      />
    </div>

    <!-- Edit mode -->
    <div
      v-else-if="viewMode === 'edit' && currentAutomation"
      class="flex h-full flex-col"
    >
      <!-- Inline editing form -->
      <div
        v-if="isEditingInline"
        class="flex-1 overflow-y-auto p-6"
      >
        <AutomationForm
          mode="edit"
          :initial-values="currentAutomation"
          :submit-error="formError"
          @submit="handleUpdate"
          @cancel="handleCancelEdit"
        />
      </div>

      <!-- Detail view -->
      <div
        v-else
        class="flex flex-1 flex-col overflow-y-auto"
      >
        <!-- Header with actions -->
        <div class="border-b p-6">
          <div class="mb-4 flex items-start justify-between">
            <div class="flex-1">
              <h2 class="text-2xl font-semibold">
                {{ currentAutomation.name }}
              </h2>
              <div class="mt-2 flex items-center gap-2">
                <Switch
                  :model-value="currentAutomation.isEnabled"
                  :disabled="isTogglingEnabled"
                  @update:model-value="(val: boolean) => handleToggleEnabled(val)"
                />
                <span class="text-sm text-muted-foreground">
                  {{ currentAutomation.isEnabled ? 'Enabled' : 'Disabled' }}
                </span>
              </div>
            </div>
            <div class="flex gap-2">
              <Button
                variant="outline"
                size="icon"
                title="Run now"
                @click="handlePlay"
              >
                <Play class="h-4 w-4" />
              </Button>
              <Button
                variant="outline"
                size="icon"
                title="Edit"
                @click="startEdit"
              >
                <Edit class="h-4 w-4" />
              </Button>
              <Button
                variant="outline"
                size="icon"
                title="Delete"
                @click="startDelete(currentAutomation.id)"
              >
                <Trash2 class="h-4 w-4" />
              </Button>
            </div>
          </div>
        </div>

        <!-- Content sections -->
        <div class="flex-1 space-y-6 p-6">
          <div
            v-if="actionMessage"
            :class="[
              'flex items-start gap-3 border px-4 py-3 text-sm',
              actionMessage.kind === 'error'
                ? 'border-destructive/30 bg-destructive/10 text-destructive'
                : 'border-border bg-card text-muted-foreground',
            ]"
            :role="actionMessage.kind === 'error' ? 'alert' : 'status'"
            data-testid="automation-action-message"
          >
            <AlertCircle
              v-if="actionMessage.kind === 'error'"
              class="mt-0.5 h-4 w-4 shrink-0"
            />
            <p>{{ actionMessage.text }}</p>
          </div>

          <!-- Prompt section -->
          <div class="rounded-lg border bg-card p-4">
            <h3 class="mb-2 text-sm font-medium text-muted-foreground">
              Prompt
            </h3>
            <p class="whitespace-pre-wrap text-sm">
              {{ currentAutomation.prompt }}
            </p>
          </div>

          <!-- When & Where section -->
          <div class="rounded-lg border bg-card p-4">
            <h3 class="mb-3 text-sm font-medium text-muted-foreground">
              When & Where
            </h3>
            <div class="space-y-3 text-sm">
              <div>
                <Badge>{{ currentAutomation.triggerType }}</Badge>
              </div>
              <div>
                <span class="font-medium">{{ currentAutomation.triggerType === 'event' ? 'When:' : 'Configuration:' }}</span>
                <span
                  v-if="currentAutomation.triggerType === 'event'"
                  class="ml-2"
                  data-testid="automation-trigger-summary"
                >{{ triggerSummary }}</span>
                <code
                  v-else
                  class="ml-2 rounded bg-muted px-1.5 py-0.5 text-xs font-mono"
                  data-testid="automation-trigger-summary"
                >{{ triggerSummary }}</code>
              </div>
              <div>
                <span class="text-muted-foreground">Target:</span>
                <span class="ml-2">{{ formatTargetType(currentAutomation.targetType) }}</span>
              </div>
            </div>
          </div>

          <!-- Policy section -->
          <div class="rounded-lg border bg-card p-4">
            <h3 class="mb-3 text-sm font-medium text-muted-foreground">
              Policy
            </h3>
            <div class="grid grid-cols-3 gap-4">
              <div>
                <div class="text-xs text-muted-foreground">Max concurrent runs</div>
                <div class="text-sm font-medium">{{ currentAutomation.maxConcurrentRuns ?? 'Unlimited' }}</div>
              </div>
              <div>
                <div class="text-xs text-muted-foreground">Max runs per hour</div>
                <div class="text-sm font-medium">{{ currentAutomation.maxRunsPerHour ?? 'Unlimited' }}</div>
              </div>
              <div>
                <div class="text-xs text-muted-foreground">Timeout</div>
                <div class="text-sm font-medium">{{ currentAutomation.timeoutMinutes ?? 'None' }} minutes</div>
              </div>
            </div>
          </div>

          <!-- Metadata footer -->
          <div class="pt-4 border-t">
            <div class="flex flex-wrap gap-2 items-center">
              <Badge
                v-if="currentAutomation.workspaceId"
                variant="outline"
              >
                Workspace: {{ currentAutomation.workspaceId }}
              </Badge>
              <Badge
                v-if="currentAutomation.model"
                variant="outline"
              >
                Model: {{ currentAutomation.model }}
              </Badge>
              <Badge
                v-if="currentAutomation.agent"
                variant="outline"
              >
                Agent: {{ currentAutomation.agent }}
              </Badge>
              <template v-if="currentAutomation.targetTags && currentAutomation.targetTags.length > 0">
                <Badge
                  v-for="tag in currentAutomation.targetTags"
                  :key="tag"
                  variant="outline"
                >
                  Tag: {{ tag }}
                </Badge>
              </template>
              <span class="text-xs text-muted-foreground">
                Created {{ new Date(currentAutomation.createdAt).toLocaleDateString() }}
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Delete confirmation dialog -->
    <AlertDialog v-model:open="deleteConfirmOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete Automation</AlertDialogTitle>
          <AlertDialogDescription>
            Are you sure you want to delete this automation? This action cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction @click="confirmDelete">
            Delete
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>
