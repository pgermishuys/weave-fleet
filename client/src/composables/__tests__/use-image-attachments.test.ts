import { effectScope, shallowRef } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useImageAttachments } from "../use-image-attachments";

function png(name = "shot.png", size = 8): File {
  return new File([new Uint8Array(size)], name, { type: "image/png" });
}

async function settle(): Promise<void> {
  await vi.waitFor(() => undefined);
  await new Promise((resolve) => setTimeout(resolve, 20));
}

describe("useImageAttachments", () => {
  let key = "";

  beforeEach(() => {
    key = `draft-${Math.random()}`;
    URL.createObjectURL = vi.fn(() => "blob:preview");
    URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => vi.useRealTimers());

  function setup(supports = true) {
    const supportsImages = shallowRef(supports);
    return { supportsImages, ...useImageAttachments(key, supportsImages) };
  }

  it("holds an image as base64 with its name", async () => {
    const { attachments, addImage, pasteError } = setup();

    addImage(png("login-error.png"));
    await settle();

    expect(attachments.value).toHaveLength(1);
    expect(attachments.value[0]).toMatchObject({ mime: "image/png", filename: "login-error.png" });
    expect(attachments.value[0]?.data).toBeTruthy();
    expect(pasteError.value).toBeUndefined();
  });

  it("refuses an image the harness can't take", async () => {
    const { attachments, addImage, pasteError } = setup(false);

    addImage(png());
    await settle();

    expect(attachments.value).toHaveLength(0);
    expect(pasteError.value).toBe("This session's harness can't take images.");
  });

  it("refuses other types and big images", async () => {
    const { attachments, addImage, pasteError } = setup();

    addImage(new File(["x"], "notes.txt", { type: "text/plain" }));
    expect(pasteError.value).toBe("Unsupported image type: text/plain");
    addImage(png("big.png", 5 * 1024 * 1024 + 1));
    expect(pasteError.value).toMatch(/exceeds 5MB/);
    await settle();
    expect(attachments.value).toHaveLength(0);
  });

  it("forgets the error after a few seconds", () => {
    vi.useFakeTimers();
    const { addImage, pasteError } = setup(false);

    addImage(png());
    expect(pasteError.value).toBeDefined();
    vi.advanceTimersByTime(5000);

    expect(pasteError.value).toBeUndefined();
  });

  it("removes and clears images", async () => {
    const scope = effectScope();
    const { attachments, addImage, removeAttachment, clearAttachments } = scope.run(() => setup())!;

    addImage(png("a.png"));
    addImage(png("b.png"));
    await settle();
    removeAttachment(attachments.value[0]!.id);
    expect(attachments.value.map((a) => a.filename)).toEqual(["b.png"]);
    clearAttachments();

    expect(attachments.value).toHaveLength(0);
    scope.stop();
  });
});
