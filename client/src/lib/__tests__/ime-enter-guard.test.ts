import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import QuestionCard from "@/components/session/QuestionCard.vue";
import SessionDetailHeaderRename from "@/components/sessions/InlineEdit.vue";
import { installImeEnterGuard } from "@/lib/ime-enter-guard";

const questions = [{
  header: "Deploy target",
  question: "Which environment should we deploy to?",
  options: [{ label: "Staging", description: "Safe." }, { label: "Production", description: "Real." }],
}];

function mountCard(onSubmit = vi.fn(async () => {})) {
  const wrapper = mount(QuestionCard, {
    props: {
      part: { partId: "p1", type: "tool", tool: "question", callId: "c1", state: { status: "running", input: { questions } } },
      sessionId: "s1",
      onSubmit,
      onDismiss: vi.fn(async () => {}),
    },
    attachTo: document.body,
  });
  return { wrapper, onSubmit };
}

describe("Enter that confirms an IME composition", () => {
  let uninstall: () => void;
  beforeEach(() => { uninstall = installImeEnterGuard(window); });
  afterEach(() => uninstall());

  it("does not send a question answer", async () => {
    const { wrapper, onSubmit } = mountCard();
    const card = wrapper.get("[data-testid='question-card-active']");
    await card.trigger("keydown", { key: "2" });

    await card.trigger("keydown", { key: "Enter", isComposing: true });
    await card.trigger("keydown", { key: "Enter", keyCode: 229 });
    expect(onSubmit).not.toHaveBeenCalled();

    await card.trigger("keydown", { key: "Enter" });
    expect(onSubmit).toHaveBeenCalledWith([["Production"]]);
    wrapper.unmount();
  });

  it("does not commit a rename", async () => {
    const wrapper = mount(SessionDetailHeaderRename, { props: { initialValue: "Old" }, attachTo: document.body });
    const input = wrapper.get("input");
    await input.setValue("新しい");
    await input.trigger("keydown", { key: "Enter", isComposing: true });
    expect(wrapper.emitted("commit")).toBeUndefined();
    wrapper.unmount();
  });

  it("leaves arrow keys and a plain Enter alone", () => {
    const seen: string[] = [];
    const listener = (e: KeyboardEvent) => seen.push(e.key);
    document.body.addEventListener("keydown", listener);
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", isComposing: true, bubbles: true }));
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    document.body.removeEventListener("keydown", listener);
    expect(seen).toEqual(["ArrowDown", "Enter"]);
  });
});
