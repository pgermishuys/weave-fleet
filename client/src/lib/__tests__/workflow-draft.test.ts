import { describe, expect, it } from "vitest";
import {
  dropUnusedMax,
  isLoop,
  loopOutcomes,
  newAgentStep,
  newYouStep,
  removeOutcome,
  renameOutcome,
  renameStepId,
  slugOf,
  targetTitle,
  uniqueId,
  updateStep,
  variablesFor,
} from "@/lib/workflow-draft";
import { reviewDraft } from "@/components/workflows/__tests__/workflow-draft-fixtures";

describe("workflow drafts", () => {
  it("renames a step id and every reference follows: routes, choices and step variables", () => {
    const renamed = renameStepId(reviewDraft(), "plan", "outline");

    expect(renamed.steps.map((step) => step.id)).toEqual(["outline", "ok-plan", "implement", "review"]);
    expect(renamed.steps[1].choices.map((choice) => choice.to)).toEqual(["implement", "outline"]);
    expect(renamed.steps[2].prompt).toBe("Build {{steps.outline.files}}.\nThe request: {{request}}\n");
    expect(renamed.steps[3].prompt).toBe("Review against {{ steps.outline.files }} and {{steps.outline.summary}}.\n");

    const implement = renameStepId(reviewDraft(), "implement", "build");
    expect(implement.steps[3].routes).toEqual({ changes: "build" });
    expect(implement.steps[1].choices[0].to).toBe("build");
  });

  it("leaves a step id that only starts the same alone", () => {
    const draft = reviewDraft();
    draft.steps[2].prompt = "{{steps.plan-b.files}} and {{steps.plan.files}}";
    expect(renameStepId(draft, "plan", "outline").steps[2].prompt).toBe("{{steps.plan-b.files}} and {{steps.outline.files}}");
  });

  it("knows a loop: back to an earlier step or itself, not forward or to the end", () => {
    const draft = reviewDraft();
    expect(isLoop(draft, 3, "implement")).toBe(true);
    expect(isLoop(draft, 3, "review")).toBe(true);
    expect(isLoop(draft, 0, "implement")).toBe(false);
    expect(isLoop(draft, 3, "end")).toBe(false);
    expect(loopOutcomes(draft, 3)).toEqual(["changes"]);
  });

  it("renames and removes outcomes with where they lead", () => {
    const review = reviewDraft().steps[3];
    expect(renameOutcome(review, "changes", "fix")).toEqual({ outcomes: ["pass", "fix"], routes: { fix: "implement" } });
    expect(removeOutcome(review, "changes")).toEqual({ outcomes: ["pass"], routes: {} });
  });

  it("drops a max once the step has no loop, and keeps it empty for a new loop", () => {
    const draft = reviewDraft();
    const noLoop = dropUnusedMax(updateStep(draft, 3, { routes: {} }), 3);
    expect(noLoop.steps[3].maxLoops).toBeNull();
    expect(dropUnusedMax(draft, 3).steps[3].maxLoops).toBe(2);
  });

  it("offers the run's variables and the earlier steps' ones", () => {
    const variables = variablesFor(reviewDraft(), 3);
    expect(variables).toContain("request");
    expect(variables).toContain("steps.plan.files");
    expect(variables).toContain("steps.implement.summary");
    expect(variables).not.toContain("steps.implement.files");
    expect(variables).not.toContain("steps.review.summary");
  });

  it("adds steps with ids no other step has", () => {
    const draft = reviewDraft();
    expect(uniqueId(draft, "plan")).toBe("plan-2");
    const agent = newAgentStep(draft);
    expect(agent).toMatchObject({ id: "step", title: "New step", model: "standard", outcomes: ["done"] });
    expect(agent.prompt).toContain("{{request}}");
    expect(newYouStep(draft, "review").choices).toEqual([
      { label: "Carry on", to: "review", note: false },
      { label: "Stop here", to: "end", note: false },
    ]);
  });

  it("names targets for people", () => {
    expect(targetTitle(reviewDraft(), "implement")).toBe("Implement");
    expect(targetTitle(reviewDraft(), "end")).toBe("End the run");
    expect(targetTitle(reviewDraft(), undefined)).toBe("Next step");
  });

  it("makes the same slug as the server", () => {
    expect(slugOf("Tidy up a flaky test")).toBe("tidy-up-a-flaky-test");
    expect(slugOf("Build a feature, our way")).toBe("build-a-feature-our-way");
    expect(slugOf("  --Weird!! Name__ ")).toBe("weird-name");
    expect(slugOf("!!!")).toBe("workflow");
  });
});
