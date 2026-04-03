"use client";

import { useRef, useCallback, useEffect } from "react";

interface UseSidebarResizeOptions {
  minWidth: number;
  maxWidth: number;
  onResize: (width: number) => void;
  onResizeEnd: (width: number) => void;
  onResizeStart: () => void;
  disabled?: boolean;
  /**
   * Pixel offset subtracted from `clientX` before clamping.
   * Use this when the resizable element does not start at the left viewport edge.
   * Default: 0
   */
  offset?: number;
}

export function useSidebarResize({
  minWidth,
  maxWidth,
  onResize,
  onResizeEnd,
  onResizeStart,
  disabled = false,
  offset = 0,
}: UseSidebarResizeOptions) {
  const isResizingRef = useRef(false);
  const latestWidthRef = useRef(0);

  const handlePointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      if (disabled) return;
      e.preventDefault();
      e.stopPropagation();

      isResizingRef.current = true;
      onResizeStart();

      // Capture pointer to receive events even outside the element
      (e.target as HTMLElement).setPointerCapture(e.pointerId);

      // Prevent text selection and set resize cursor globally
      document.body.style.cursor = "col-resize";
      document.body.style.userSelect = "none";
    },
    [disabled, onResizeStart]
  );

  const handlePointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      if (!isResizingRef.current) return;
      e.preventDefault();

      const newWidth = Math.round(
        Math.min(maxWidth, Math.max(minWidth, e.clientX - offset))
      );
      latestWidthRef.current = newWidth;
      onResize(newWidth);
    },
    [minWidth, maxWidth, onResize, offset]
  );

  const handlePointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      if (!isResizingRef.current) return;

      isResizingRef.current = false;
      (e.target as HTMLElement).releasePointerCapture(e.pointerId);

      // Restore normal cursor and selection
      document.body.style.cursor = "";
      document.body.style.userSelect = "";

      onResizeEnd(latestWidthRef.current);
    },
    [onResizeEnd]
  );

  // Safety cleanup on unmount
  useEffect(() => {
    return () => {
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    };
  }, []);

  return {
    handlePointerDown,
    handlePointerMove,
    handlePointerUp,
  };
}
