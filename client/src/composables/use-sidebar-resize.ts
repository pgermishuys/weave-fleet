import { onUnmounted } from "vue";

export interface UseSidebarResizeOptions {
  minWidth: number;
  maxWidth: number;
  onResize: (width: number) => void;
  onResizeEnd: (width: number) => void;
  onResizeStart: () => void;
  disabled?: boolean;
  offset?: number;
}

export interface UseSidebarResizeResult {
  handlePointerDown: (event: PointerEvent) => void;
  handlePointerMove: (event: PointerEvent) => void;
  handlePointerUp: (event: PointerEvent) => void;
}

export function useSidebarResize({
  minWidth,
  maxWidth,
  onResize,
  onResizeEnd,
  onResizeStart,
  disabled = false,
  offset = 0,
}: UseSidebarResizeOptions): UseSidebarResizeResult {
  let isResizing = false;
  let latestWidth = minWidth;

  function clampWidth(clientX: number): number {
    return Math.round(Math.min(maxWidth, Math.max(minWidth, clientX - offset)));
  }

  function resetBodyStyles(): void {
    document.body.style.cursor = "";
    document.body.style.userSelect = "";
  }

  function handlePointerDown(event: PointerEvent): void {
    if (disabled) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    isResizing = true;
    latestWidth = clampWidth(event.clientX);
    onResizeStart();

    const currentTarget = event.currentTarget;
    if (currentTarget instanceof HTMLElement) {
      currentTarget.setPointerCapture(event.pointerId);
    }

    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";
  }

  function handlePointerMove(event: PointerEvent): void {
    if (!isResizing) {
      return;
    }

    event.preventDefault();
    latestWidth = clampWidth(event.clientX);
    onResize(latestWidth);
  }

  function handlePointerUp(event: PointerEvent): void {
    if (!isResizing) {
      return;
    }

    isResizing = false;

    const currentTarget = event.currentTarget;
    if (currentTarget instanceof HTMLElement) {
      currentTarget.releasePointerCapture(event.pointerId);
    }

    resetBodyStyles();
    onResizeEnd(latestWidth);
  }

  onUnmounted(() => {
    resetBodyStyles();
  });

  return {
    handlePointerDown,
    handlePointerMove,
    handlePointerUp,
  };
}
