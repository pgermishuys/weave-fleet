import { onMounted, onUnmounted, readonly, shallowRef, type ShallowRef } from "vue";

const queryListeners = new Map<string, Set<() => void>>();
const queryLists = new Map<string, MediaQueryList>();
const queryHandlers = new Map<string, () => void>();

function getMediaQueryList(query: string): MediaQueryList | null {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") {
    return null;
  }

  let mediaQueryList = queryLists.get(query);
  if (!mediaQueryList) {
    mediaQueryList = window.matchMedia(query);
    queryLists.set(query, mediaQueryList);
  }

  return mediaQueryList;
}

function subscribeToQuery(query: string, callback: () => void): () => void {
  let listeners = queryListeners.get(query);
  if (!listeners) {
    listeners = new Set();
    queryListeners.set(query, listeners);
  }

  listeners.add(callback);

  const mediaQueryList = getMediaQueryList(query);
  if (mediaQueryList && !queryHandlers.has(query)) {
    const handler = () => {
      const activeListeners = queryListeners.get(query);
      if (!activeListeners) {
        return;
      }

      for (const listener of activeListeners) {
        listener();
      }
    };

    mediaQueryList.addEventListener("change", handler);
    queryHandlers.set(query, handler);
  }

  return () => {
    const activeListeners = queryListeners.get(query);
    activeListeners?.delete(callback);

    if (!activeListeners || activeListeners.size > 0) {
      return;
    }

    queryListeners.delete(query);

    const handler = queryHandlers.get(query);
    if (mediaQueryList && handler) {
      mediaQueryList.removeEventListener("change", handler);
    }

    queryHandlers.delete(query);
  };
}

export function useMediaQuery(query: string): Readonly<ShallowRef<boolean>> {
  const matches = shallowRef(false);
  let unsubscribe: (() => void) | undefined;

  function updateMatches(): void {
    matches.value = getMediaQueryList(query)?.matches ?? false;
  }

  onMounted(() => {
    updateMatches();
    unsubscribe = subscribeToQuery(query, updateMatches);
  });

  onUnmounted(() => {
    unsubscribe?.();
  });

  return readonly(matches);
}

export function useIsMobile(): Readonly<ShallowRef<boolean>> {
  return useMediaQuery("(max-width: 767px)");
}

export function useIsMobileNav(): Readonly<ShallowRef<boolean>> {
  return useMediaQuery("(max-width: 716px)");
}

export function useIsTablet(): Readonly<ShallowRef<boolean>> {
  return useMediaQuery("(min-width: 768px) and (max-width: 1023px)");
}

export function useIsDesktop(): Readonly<ShallowRef<boolean>> {
  return useMediaQuery("(min-width: 1024px)");
}
