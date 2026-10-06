import { describe, expect, it, vi } from "vitest";
import { captureInstallPrompt, installed, installPrompt, promptInstall } from "../install-prompt";

describe("installing Fleet on Android", () => {
  it("keeps Chrome's offer for the setup screen and shows it on request", async () => {
    const target = new EventTarget() as unknown as Window;
    captureInstallPrompt(target);

    const offer = Object.assign(new Event("beforeinstallprompt", { cancelable: true }), {
      prompt: vi.fn(async () => undefined),
      userChoice: Promise.resolve({ outcome: "accepted" as const }),
    });
    target.dispatchEvent(offer);

    expect(offer.defaultPrevented).toBe(true);
    expect(installPrompt.value).toBe(offer);
    expect(await promptInstall()).toBe(true);
    expect(offer.prompt).toHaveBeenCalledOnce();
    expect(installed.value).toBe(true);
    expect(installPrompt.value).toBeNull();
    expect(await promptInstall()).toBe(false);
  });
});
