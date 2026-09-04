import { describe, expect, it } from "vitest";
import { useVisualPanel } from "@/composables/use-visual-panel";
import type { VisualPayload } from "@/lib/visual-payload";

function payload(title: string): VisualPayload {
  return { $type: "markdown", content: `content for ${title}`, title };
}

describe("use-visual-panel", () => {
  it("showVisual on session A does not populate session B's visualPayload", () => {
    const sessionA = "session-a-1";
    const sessionB = "session-b-1";

    const a = useVisualPanel(sessionA);
    const b = useVisualPanel(sessionB);

    a.showVisual(payload("A"));

    expect(a.visualPayload.value).toEqual(payload("A"));
    expect(b.visualPayload.value).toBeNull();
  });

  it("showVisual on B then A keeps each session's own payload", () => {
    const sessionA = "session-a-2";
    const sessionB = "session-b-2";

    const a = useVisualPanel(sessionA);
    const b = useVisualPanel(sessionB);

    b.showVisual(payload("B"));
    a.showVisual(payload("A"));

    expect(useVisualPanel(sessionA).visualPayload.value).toEqual(payload("A"));
    expect(useVisualPanel(sessionB).visualPayload.value).toEqual(payload("B"));
  });

  it("clearVisual on session A does not clear session B's payload", () => {
    const sessionA = "session-a-3";
    const sessionB = "session-b-3";

    const a = useVisualPanel(sessionA);
    const b = useVisualPanel(sessionB);

    a.showVisual(payload("A"));
    b.showVisual(payload("B"));

    a.clearVisual();

    expect(a.visualPayload.value).toBeNull();
    expect(useVisualPanel(sessionB).visualPayload.value).toEqual(payload("B"));
  });

  it("does not orphan a previously captured ref after clearVisual + a later showVisual", () => {
    const sessionId = "session-orphan-1";

    // Consumer captures the ref once, as a computed would.
    const consumerRef = useVisualPanel(sessionId).visualPayload;

    const first = useVisualPanel(sessionId);
    first.showVisual(payload("first"));
    expect(consumerRef.value).toEqual(payload("first"));

    first.clearVisual();
    expect(consumerRef.value).toBeNull();

    // A later call must reuse the same ref, not create a new detached one.
    const second = useVisualPanel(sessionId);
    second.showVisual(payload("second"));

    expect(consumerRef.value).toEqual(payload("second"));
  });
});
