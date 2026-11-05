import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import QuickAnswersComponent from "../QuestionQuickAnswers.vue";

// CI's older @vue/test-utils rejects this component's generated props type
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const QuestionQuickAnswers = QuickAnswersComponent as any;

const opt = (label: string, description = "") => ({ label, description });
const base = { sent: null, busy: false, moreTestId: "more" };

describe("QuestionQuickAnswers", () => {
  it("stacks up to four options as full-width rows with their whole label and description", async () => {
    const w = mount(QuestionQuickAnswers, {
      props: {
        ...base,
        question: { question: "Which?", options: [opt("ISO (YYYY-MM-DD)", "2026-11-05"), opt("Relative (in 3 days)"), opt("Long")] },
      },
    });
    expect(w.find('[data-testid="quick-stacked"]').exists()).toBe(true);
    expect(w.findAll(".ph-qa__row")).toHaveLength(4);
    expect(w.text()).toContain("ISO (YYYY-MM-DD)");
    expect(w.text()).toContain("2026-11-05");
    await w.findAll(".ph-qa__row")[1]!.trigger("click");
    expect(w.emitted("pick")![0]).toEqual(["Relative (in 3 days)"]);
    await w.find('[data-testid="more"]').trigger("click");
    expect(w.emitted("more")).toHaveLength(1);
  });

  it("keeps the compact two-button row past four options", () => {
    const w = mount(QuestionQuickAnswers, {
      props: { ...base, question: { question: "Which?", options: ["a", "b", "c", "d", "e"].map((l) => opt(l)) } },
    });
    expect(w.find('[data-testid="quick-stacked"]').exists()).toBe(false);
    expect(w.findAll("button").map((b) => b.text())).toEqual(["a", "b", "More…"]);
  });

  it("keeps Answer… alone for a several-answers question", () => {
    const w = mount(QuestionQuickAnswers, {
      props: { ...base, question: { question: "Which?", multiple: true, options: [opt("a"), opt("b")] } },
    });
    expect(w.findAll("button").map((b) => b.text())).toEqual(["Answer…"]);
  });
});
