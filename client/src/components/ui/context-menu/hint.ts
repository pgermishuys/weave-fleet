import { inject, provide, shallowRef, type InjectionKey, type ShallowRef } from "vue"

/** The words a menu's footer (ContextMenuHint) shows for its highlighted row; null shows the footer's own. */
const hintKey: InjectionKey<ShallowRef<string | null> | null> = Symbol("context-menu-hint")

/** Called by a menu panel: its rows tell its footer about themselves. */
export function provideMenuHint(): void {
  provide(hintKey, shallowRef(null))
}

/** Called by a submenu panel: its rows leave the footer alone, so it keeps the words of the row that opened it. */
export function provideNoMenuHint(): void {
  provide(hintKey, null)
}

export function injectMenuHint(): ShallowRef<string | null> | null {
  return inject(hintKey, null)
}

/**
 * Focus handlers for a row with a hint. reka focuses the highlighted row whether the pointer or the keyboard moved
 * there, so focus is when the footer changes. `keepOnBlur` lets a submenu's row keep its words while its submenu is open.
 */
export function useMenuRowHint(hint: () => string | undefined, keepOnBlur?: (row: HTMLElement) => boolean) {
  const shown = injectMenuHint()
  return {
    onFocus(): void {
      if (shown) shown.value = hint() ?? null
    },
    onBlur(event: FocusEvent): void {
      if (!shown || shown.value !== (hint() ?? null)) return
      if (keepOnBlur?.(event.currentTarget as HTMLElement)) return
      shown.value = null
    },
  }
}
