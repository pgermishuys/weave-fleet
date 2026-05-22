import { computed, inject, onScopeDispose, provide, shallowRef, type InjectionKey, type Ref } from "vue";
import type { UseDiffsResult } from "@/composables/use-diffs";

export interface SessionDiffsContext {
  diffState: UseDiffsResult;
  isDiffsTrayOpen?: Readonly<Ref<boolean>>;
  openDiffsTray?: () => void;
}

export const SessionDiffsContextKey: InjectionKey<SessionDiffsContext> = Symbol("SessionDiffsContext");

const activeSessionDiffsContext = shallowRef<SessionDiffsContext | null>(null);

export function provideSessionDiffsContext(
  diffState: UseDiffsResult,
  options: Omit<SessionDiffsContext, "diffState"> = {},
): SessionDiffsContext {
  const context: SessionDiffsContext = { diffState, ...options };

  provide(SessionDiffsContextKey, context);
  activeSessionDiffsContext.value = context;

  onScopeDispose(() => {
    if (activeSessionDiffsContext.value === context) {
      activeSessionDiffsContext.value = null;
    }
  });

  return context;
}

export function useSessionDiffsContext(): Readonly<Ref<SessionDiffsContext | null>> {
  const injectedContext = inject(SessionDiffsContextKey, null);

  if (injectedContext) {
    return computed(() => injectedContext);
  }

  return computed(() => activeSessionDiffsContext.value);
}
