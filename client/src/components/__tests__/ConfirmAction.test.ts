import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConfirmHost, confirmAction, type ConfirmOptions } from "@/components/ui/confirm";

enableAutoUnmount(afterEach);

const deleteCard: ConfirmOptions = {
  title: "Delete this card?",
  subject: "Tidy the release notes",
  body: "It's removed from the board.",
  confirmLabel: "Delete card",
  destructive: true,
};

function mountHost() {
  return mount(ConfirmHost, { attachTo: document.body, global: { stubs: { teleport: false } } });
}

function dialog(): HTMLElement | null {
  return document.body.querySelector<HTMLElement>("[data-testid='confirm-dialog']");
}

function button(id: "confirm-action" | "confirm-cancel"): HTMLButtonElement {
  return document.body.querySelector<HTMLButtonElement>(`[data-testid='${id}']`)!;
}

/** Opens a confirm and waits for it to be on screen, focus placed. The answer comes wrapped: awaiting a bare
 * promise from an async function would wait for the answer itself. */
async function ask(options: ConfirmOptions): Promise<{ answer: Promise<boolean> }> {
  const answer = confirmAction(options);
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
  return { answer };
}

describe("confirmAction", () => {
  it("draws the question, the subject, what happens and a button that says the action", async () => {
    mountHost();
    await ask(deleteCard);

    expect(dialog()?.getAttribute("role")).toBe("alertdialog");
    expect(dialog()?.textContent).toContain("Delete this card?");
    expect(dialog()?.textContent).toContain("Tidy the release notes");
    expect(dialog()?.textContent).toContain("It's removed from the board.");
    expect(button("confirm-action").textContent?.trim()).toBe("Delete card");
    expect(button("confirm-cancel").textContent?.trim()).toBe("Cancel");
    expect(button("confirm-action").dataset.variant).toBe("destructive");
  });

  it("resolves true when the action is taken, and closes", async () => {
    mountHost();
    const { answer } = await ask(deleteCard);

    button("confirm-action").click();

    await expect(answer).resolves.toBe(true);
    await flushPromises();
    expect(dialog()).toBeNull();
  });

  it("resolves false on Cancel", async () => {
    mountHost();
    const { answer } = await ask(deleteCard);

    button("confirm-cancel").click();

    await expect(answer).resolves.toBe(false);
  });

  it("resolves false on Esc", async () => {
    mountHost();
    const { answer } = await ask(deleteCard);

    document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));

    await expect(answer).resolves.toBe(false);
  });

  it("starts focus on Cancel for a destructive action, so Enter doesn't take it by accident", async () => {
    mountHost();
    await ask(deleteCard);

    expect(document.activeElement).toBe(button("confirm-cancel"));
  });

  it("starts focus on the action when it isn't destructive", async () => {
    mountHost();
    await ask({ ...deleteCard, title: "Replace the draft?", confirmLabel: "Replace draft", destructive: false });

    expect(document.activeElement).toBe(button("confirm-action"));
    expect(button("confirm-action").dataset.variant).toBe("default");
  });

  it("puts focus back where it was when the dialog closes", async () => {
    const trigger = document.createElement("button");
    trigger.textContent = "Delete";
    document.body.appendChild(trigger);
    trigger.focus();
    mountHost();
    const { answer } = await ask(deleteCard);
    expect(document.activeElement).not.toBe(trigger);

    button("confirm-cancel").click();
    await answer;
    await flushPromises();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.activeElement).toBe(trigger);
    trigger.remove();
  });

  it("asked from a menu item, puts focus back on the button that opened the menu", async () => {
    const opener = document.createElement("button");
    opener.textContent = "More";
    opener.setAttribute("aria-controls", "menu-1");
    const menu = document.createElement("div");
    menu.id = "menu-1";
    menu.setAttribute("role", "menu");
    const item = document.createElement("div");
    item.setAttribute("role", "menuitem");
    item.tabIndex = -1;
    menu.appendChild(item);
    document.body.append(opener, menu);
    item.focus();
    mountHost();
    const { answer } = await ask(deleteCard);
    menu.remove(); // the menu closes once an item is chosen

    button("confirm-cancel").click();
    await answer;
    await flushPromises();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.activeElement).toBe(opener);
    opener.remove();
  });

  it("a second ask cancels the first", async () => {
    mountHost();
    const { answer: first } = await ask(deleteCard);
    const { answer: second } = await ask({ ...deleteCard, title: "Delete this lane?", confirmLabel: "Delete lane" });

    await expect(first).resolves.toBe(false);
    expect(dialog()?.textContent).toContain("Delete this lane?");
    button("confirm-action").click();
    await expect(second).resolves.toBe(true);
  });

  it("with no host mounted it cancels instead of waiting for ever", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});

    await expect(confirmAction(deleteCard)).resolves.toBe(false);
    expect(error).toHaveBeenCalled();
    error.mockRestore();
  });

  describe("with run", () => {
    it("holds the dialog open and busy while the action runs, then resolves true", async () => {
      mountHost();
      let finish!: () => void;
      const run = vi.fn(() => new Promise<void>((resolve) => { finish = resolve; }));
      const { answer } = await ask({ ...deleteCard, busyLabel: "Deleting…", run });

      button("confirm-action").click();
      await flushPromises();

      expect(run).toHaveBeenCalledOnce();
      expect(dialog()).not.toBeNull();
      expect(button("confirm-action").disabled).toBe(true);
      expect(button("confirm-cancel").disabled).toBe(true);
      expect(button("confirm-action").textContent).toContain("Deleting…");
      // Esc waits too: the action is already on its way.
      button("confirm-action").dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
      await flushPromises();
      expect(dialog()).not.toBeNull();

      finish();
      await expect(answer).resolves.toBe(true);
    });

    it("keeps the dialog open with the error when the action fails, to try again or cancel", async () => {
      mountHost();
      const run = vi.fn()
        .mockRejectedValueOnce(new Error("The session is still running"))
        .mockResolvedValueOnce(undefined);
      const { answer } = await ask({ ...deleteCard, errorMessage: "Couldn't delete the card.", run });

      button("confirm-action").click();
      await flushPromises();

      const error = document.body.querySelector("[data-testid='confirm-error']");
      expect(error?.textContent).toContain("Couldn't delete the card.");
      expect(error?.textContent).toContain("The session is still running");
      expect(button("confirm-action").disabled).toBe(false);

      button("confirm-action").click();
      await expect(answer).resolves.toBe(true);
      expect(run).toHaveBeenCalledTimes(2);
    });

    it("resolves false when cancelled after a failure", async () => {
      mountHost();
      const { answer } = await ask({ ...deleteCard, run: () => Promise.reject(new Error("offline")) });

      button("confirm-action").click();
      await flushPromises();
      button("confirm-cancel").click();

      await expect(answer).resolves.toBe(false);
    });
  });
});
