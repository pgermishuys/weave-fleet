import { inject, provide, type InjectionKey } from "vue";
import type { AnnotationAnchor } from "@/lib/annotation-types";

/**
 * Lets any canvas start an annotation (select text, then ask the agent about
 * it) without owning the popover. The right panel provides the handler.
 */
export type CanvasAnnotateHandler = (
  anchor: AnnotationAnchor,
  position: { x: number; y: number },
  sourceFilePath: string,
) => void;

const CanvasAnnotateKey: InjectionKey<CanvasAnnotateHandler> = Symbol("CanvasAnnotate");

export function provideCanvasAnnotate(handler: CanvasAnnotateHandler): void {
  provide(CanvasAnnotateKey, handler);
}

export function useCanvasAnnotate(): CanvasAnnotateHandler {
  return inject(CanvasAnnotateKey, () => {});
}
