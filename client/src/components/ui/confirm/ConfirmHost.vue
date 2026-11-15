<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue"
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { ErrorNote } from "@/components/ui/error-note"
import { Spinner } from "@/components/ui/spinner"
import { activeConfirm, registerConfirmHost, type ConfirmOptions } from "./confirm-action"

// Draws whatever confirmAction() asks. Mounted once, in App.vue.
const unregister = registerConfirmHost()
onBeforeUnmount(() => {
  unregister()
  activeConfirm.value?.settle(false)
})

// The last ask stays drawn while the dialog fades out, so its words don't vanish first.
const shown = shallowRef<ConfirmOptions | null>(null)
const busy = shallowRef(false)
const error = shallowRef<string | null>(null)
const cancelButton = shallowRef<{ $el: HTMLElement } | null>(null)
const actionButton = shallowRef<{ $el: HTMLElement } | null>(null)

// Kept past settle(), which clears activeConfirm before the dialog's close-focus event fires.
let returnFocus: HTMLElement | null = null

watch(activeConfirm, (request) => {
  busy.value = false
  error.value = null
  if (!request) return
  shown.value = request.options
  returnFocus = request.returnFocus
})

const open = computed(() => activeConfirm.value !== null)

function onOpenChange(value: boolean): void {
  if (value || busy.value) return
  activeConfirm.value?.settle(false)
}

async function onConfirm(): Promise<void> {
  const request = activeConfirm.value
  if (!request || busy.value) return
  const { run } = request.options
  if (!run) {
    request.settle(true)
    return
  }
  busy.value = true
  error.value = null
  try {
    await run()
    request.settle(true)
  } catch (failure) {
    if (activeConfirm.value !== request) return
    error.value = failure instanceof Error ? failure.message : String(failure)
    busy.value = false
  }
}

/** Destructive: Cancel, so Enter doesn't take it by accident. Otherwise the action. */
function onOpenAutoFocus(event: Event): void {
  event.preventDefault()
  const target = shown.value?.destructive ? cancelButton.value : actionButton.value
  target?.$el?.focus({ preventScroll: true })
}

function onEscapeKeyDown(event: KeyboardEvent): void {
  if (busy.value) event.preventDefault()
}

/** Back where focus was when the ask came, if that's still on the page; otherwise wherever reka puts it. */
function onCloseAutoFocus(event: Event): void {
  const back = returnFocus
  returnFocus = null
  if (back?.isConnected) {
    event.preventDefault()
    back.focus({ preventScroll: true })
  }
}
</script>

<template>
  <AlertDialog
    :open="open"
    @update:open="onOpenChange"
  >
    <AlertDialogContent
      v-if="shown"
      data-testid="confirm-dialog"
      @open-auto-focus="onOpenAutoFocus"
      @escape-key-down="onEscapeKeyDown"
      @close-auto-focus="onCloseAutoFocus"
    >
      <AlertDialogHeader>
        <AlertDialogTitle>{{ shown.title }}</AlertDialogTitle>
        <AlertDialogDescription class="grid gap-1">
          <span
            v-if="shown.subject"
            data-testid="confirm-subject"
            :class="['block break-words text-foreground', shown.monoSubject ? 'font-mono' : 'font-medium']"
          >{{ shown.subject }}</span>
          <span class="block">{{ shown.body }}</span>
        </AlertDialogDescription>
      </AlertDialogHeader>
      <ErrorNote
        v-if="error"
        :message="shown.errorMessage ?? error"
        :reason="shown.errorMessage ? error : undefined"
        data-testid="confirm-error"
      />
      <AlertDialogFooter>
        <Button
          ref="cancelButton"
          type="button"
          variant="outline"
          :disabled="busy"
          data-testid="confirm-cancel"
          @click="onOpenChange(false)"
        >
          {{ shown.cancelLabel ?? "Cancel" }}
        </Button>
        <Button
          ref="actionButton"
          type="button"
          :variant="shown.destructive ? 'destructive' : 'default'"
          :disabled="busy"
          :aria-busy="busy || undefined"
          data-testid="confirm-action"
          @click="onConfirm"
        >
          <Spinner
            v-if="busy"
            size="sm"
          />
          {{ busy && shown.busyLabel ? shown.busyLabel : shown.confirmLabel }}
        </Button>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
</template>
