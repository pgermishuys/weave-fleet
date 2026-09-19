import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import QuestionCard from "@/components/session/QuestionCard.vue";
import type { AccumulatedToolPart } from "@/lib/client-types";

const questions = [{
  header: "Deploy target",
  question: "Which environment should we deploy to?",
  options: [
    { label: "Staging", description: "Safe to break." },
    { label: "Production", description: "Real users." },
  ],
}];

function part(state: Record<string, unknown>): AccumulatedToolPart {
  return { partId: "p1", type: "tool", tool: "question", callId: "call-1", state };
}

function mountCard(state: Record<string, unknown>, onSubmit = vi.fn(async () => {}), onDismiss = vi.fn(async () => {})) {
  const wrapper = mount(QuestionCard, {
    props: { part: part(state), sessionId: "s1", onSubmit, onDismiss },
  });
  return { wrapper, onSubmit, onDismiss };
}

describe("QuestionCard", () => {
  it("asks for an answer and marks itself as needing you", () => {
    const { wrapper } = mountCard({ status: "running", input: { questions } });
    const card = wrapper.get("[data-testid='question-card-active']");

    expect(card.text()).toContain("Which environment should we deploy to?");
    expect(card.text()).toContain("Needs you");
    expect(card.text()).toContain("Safe to break.");
    expect(wrapper.get("[data-testid='question-submit-button']").attributes("disabled")).toBeDefined();
  });

  it("sends the picked choice", async () => {
    const { wrapper, onSubmit } = mountCard({ status: "running", input: { questions } });

    await wrapper.get("[data-testid='question-pill-Staging']").trigger("click");
    expect(wrapper.get("[data-testid='question-pill-Staging']").attributes("aria-checked")).toBe("true");
    await wrapper.get("[data-testid='question-submit-button']").trigger("click");

    expect(onSubmit).toHaveBeenCalledWith([["Staging"]]);
  });

  it("picks a choice by its number key and sends on Enter", async () => {
    const { wrapper, onSubmit } = mountCard({ status: "running", input: { questions } });
    const card = wrapper.get("[data-testid='question-card-active']");

    await card.trigger("keydown", { key: "2" });
    await card.trigger("keydown", { key: "Enter" });

    expect(onSubmit).toHaveBeenCalledWith([["Production"]]);
  });

  it("sends typed text instead of a choice", async () => {
    const { wrapper, onSubmit } = mountCard({ status: "running", input: { questions } });

    await wrapper.get("[data-testid='question-pill-Staging']").trigger("click");
    const other = wrapper.get<HTMLInputElement>(".qcard__other-input");
    await other.setValue("Both, staging first");
    await other.trigger("input");
    await wrapper.get("[data-testid='question-submit-button']").trigger("click");

    expect(onSubmit).toHaveBeenCalledWith([["Both, staging first"]]);
  });

  it("skips", async () => {
    const { wrapper, onDismiss } = mountCard({ status: "running", input: { questions } });

    await wrapper.get("[data-testid='question-dismiss-button']").trigger("click");

    expect(onDismiss).toHaveBeenCalled();
  });

  it("shrinks to a row with the question and its answer once answered", () => {
    const { wrapper } = mountCard({ status: "completed", input: { questions }, metadata: { answers: [["Staging"]] } });
    const row = wrapper.get("[data-testid='question-card-answered']");

    expect(row.text()).toContain("Asked");
    expect(row.text()).toContain("Which environment should we deploy to?");
    expect(row.text()).toContain("Staging");
    expect(wrapper.find("[data-testid='question-card-active']").exists()).toBe(false);
  });

  it("says when a question was skipped", () => {
    const { wrapper } = mountCard({ status: "error", input: { questions } });

    expect(wrapper.get("[data-testid='question-card-dismissed']").text()).toContain("Skipped");
  });
});
