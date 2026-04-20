import { onMounted, onUnmounted, shallowRef, type ShallowRef } from "vue";

interface ViewportSegment {
  top: number;
  left: number;
  bottom: number;
  right: number;
  width: number;
  height: number;
}

export interface FoldableScreenState {
  isFolded: boolean;
  foldWidth: number;
  segments: [ViewportSegment, ViewportSegment] | null;
}

const FOLD_QUERY = "(horizontal-viewport-segments: 2)";

const state = shallowRef<FoldableScreenState>({
  isFolded: false,
  foldWidth: 0,
  segments: null,
});

let mediaQueryList: MediaQueryList | null = null;
let mediaQueryHandler: (() => void) | null = null;
let consumerCount = 0;

function getFoldMql(): MediaQueryList | null {
  if (typeof window === "undefined") {
    return null;
  }

  if (!mediaQueryList) {
    mediaQueryList = window.matchMedia(FOLD_QUERY);
  }

  return mediaQueryList;
}

function getSegments(): [ViewportSegment, ViewportSegment] | null {
  if (typeof window === "undefined") {
    return null;
  }

  try {
    const visualViewport = window.visualViewport;
    const foldQuery = getFoldMql();
    if (!visualViewport || !foldQuery?.matches) {
      return null;
    }

    const foldWindow = window as Window & {
      getWindowSegments?: () => DOMRect[];
    };

    if (typeof foldWindow.getWindowSegments === "function") {
      const segments = foldWindow.getWindowSegments();
      if (segments.length === 2) {
        return [
          {
            top: segments[0].top,
            left: segments[0].left,
            bottom: segments[0].bottom,
            right: segments[0].right,
            width: segments[0].width,
            height: segments[0].height,
          },
          {
            top: segments[1].top,
            left: segments[1].left,
            bottom: segments[1].bottom,
            right: segments[1].right,
            width: segments[1].width,
            height: segments[1].height,
          },
        ];
      }
    }

    const halfWidth = Math.floor(visualViewport.width / 2);
    return [
      { top: 0, left: 0, bottom: visualViewport.height, right: halfWidth, width: halfWidth, height: visualViewport.height },
      {
        top: 0,
        left: halfWidth,
        bottom: visualViewport.height,
        right: visualViewport.width,
        width: halfWidth,
        height: visualViewport.height,
      },
    ];
  } catch {
    return null;
  }
}

function updateFoldState(): void {
  const foldQuery = getFoldMql();
  const isFolded = foldQuery?.matches ?? false;
  const segments = isFolded ? getSegments() : null;
  const foldWidth = segments ? Math.max(0, segments[1].left - segments[0].right) : 0;

  state.value = {
    isFolded,
    foldWidth,
    segments,
  };
}

function startSubscriptions(): void {
  if (mediaQueryHandler) {
    return;
  }

  const foldQuery = getFoldMql();
  if (!foldQuery) {
    return;
  }

  mediaQueryHandler = () => {
    updateFoldState();
  };

  foldQuery.addEventListener("change", mediaQueryHandler);
  window.addEventListener("resize", mediaQueryHandler);
  updateFoldState();
}

function stopSubscriptions(): void {
  if (!mediaQueryHandler) {
    return;
  }

  const foldQuery = getFoldMql();
  if (foldQuery) {
    foldQuery.removeEventListener("change", mediaQueryHandler);
  }

  window.removeEventListener("resize", mediaQueryHandler);
  mediaQueryHandler = null;
}

export function useFoldableScreen(): ShallowRef<FoldableScreenState> {
  onMounted(() => {
    consumerCount += 1;
    startSubscriptions();
  });

  onUnmounted(() => {
    consumerCount = Math.max(0, consumerCount - 1);
    if (consumerCount === 0) {
      stopSubscriptions();
    }
  });

  return state;
}
