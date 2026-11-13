import { effectScope, shallowRef } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useImageAttachments } from "../use-image-attachments";

const PNG_HEAD = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

function png(name = "shot.png", size = 8): File {
  const bytes = new Uint8Array(Math.max(size, PNG_HEAD.length));
  bytes.set(PNG_HEAD);
  return new File([bytes], name, { type: "image/png" });
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

  it("sends a PNG named .jpg as a PNG, and refuses bytes that aren't a picture", async () => {
    const { attachments, addImage, pasteError } = setup();
    const mislabelled = new File([new Uint8Array([...PNG_HEAD, 0, 0, 0, 0])], "shot.jpg", { type: "image/jpeg" });

    addImage(mislabelled);
    await settle();
    expect(attachments.value[0]).toMatchObject({ mime: "image/png", filename: "shot.jpg" });

    addImage(new File(["<svg/>"], "logo.png", { type: "image/png" }));
    await settle();
    expect(attachments.value).toHaveLength(1);
    expect(pasteError.value).toMatch(/isn't a PNG, JPEG, GIF or WebP/);
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
