import { shallowRef } from "vue"

// The one way Fleet asks "are you sure?" before an action that can't be taken back. Call sites await an answer:
//
//   if (!(await confirmAction({ title: "Delete this card?", subject: card.title, body: "It's removed from the board.",
//     confirmLabel: "Delete card", destructive: true }))) return
//
// <ConfirmHost> (mounted once, in App.vue) draws it on the shared AlertDialog: Esc and Cancel answer false, the
// action answers true. Focus starts on Cancel for a destructive action, on the action otherwise, and goes back to
// where it was when the dialog closes. Never use window.confirm: the design lint fails it.

export interface ConfirmOptions {
  /** The question, e.g. "Delete this card?" */
  title: string
  /** What happens, in a sentence or two. */
  body: string
  /** The name or path the action is about, shown on its own line. */
  subject?: string
  /** Shows the subject in monospace, for a file path. */
  monoSubject?: boolean
  /** Says the action, e.g. "Delete card", never "OK". */
  confirmLabel: string
  cancelLabel?: string
  /** Paints the action as destructive and starts focus on Cancel, so Enter doesn't take it by accident. */
  destructive?: boolean
  /**
   * Runs the action with the dialog held open: both buttons wait and Esc does nothing until it settles. Resolves true
   * when it succeeds; when it throws, the dialog stays open with the error, to try again or cancel.
   */
  run?: () => Promise<unknown>
  /** The action's label while `run` runs, e.g. "Deleting…". */
  busyLabel?: string
  /** What went wrong when `run` throws, e.g. "Couldn't delete the session." The thrown message is the reason. */
  errorMessage?: string
}

export interface ConfirmRequest {
  options: ConfirmOptions
  /** Where focus goes back to when the dialog closes. */
  returnFocus: HTMLElement | null
  settle: (confirmed: boolean) => void
}

/** The confirm on screen, if any. One at a time: a new ask cancels the one before it. */
export const activeConfirm = shallowRef<ConfirmRequest | null>(null)

let hosts = 0

/** Called by <ConfirmHost> as it mounts and unmounts. */
export function registerConfirmHost(): () => void {
  hosts += 1
  return () => { hosts -= 1 }
}

/**
 * Where focus goes back to once the confirm closes: what has it now, or, when that's an item in an open menu (which
 * is gone by then), the button that opened the menu.
 */
function focusToReturnTo(): HTMLElement | null {
  if (typeof document === "undefined") return null
  const active = document.activeElement
  if (!(active instanceof HTMLElement) || active === document.body) return null
  const menu = active.closest<HTMLElement>("[role='menu']")
  if (menu?.id) {
    const opener = Array.from(document.querySelectorAll<HTMLElement>("[aria-controls]"))
      .find((element) => element.getAttribute("aria-controls") === menu.id)
    if (opener) return opener
  }
  return active
}

/** Asks the user to confirm an action. Resolves true when they take it, false when they cancel or press Esc. */
export function confirmAction(options: ConfirmOptions): Promise<boolean> {
  if (hosts === 0) {
    console.error("confirmAction: no <ConfirmHost> is mounted, so the action is cancelled.")
    return Promise.resolve(false)
  }
  activeConfirm.value?.settle(false)
  return new Promise<boolean>((resolve) => {
    const request: ConfirmRequest = {
      options,
      returnFocus: focusToReturnTo(),
      settle: (confirmed) => {
        if (activeConfirm.value === request) activeConfirm.value = null
        resolve(confirmed)
      },
    }
    activeConfirm.value = request
  })
}
