import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import WorkflowInspector from "@/components/workflows/WorkflowInspector.vue";
import type { WorkflowDraft } from "@/lib/workflow-draft";
import { reviewDraft } from "./workflow-draft-fixtures";

function inspect(selected: number | null, draft: WorkflowDraft = reviewDraft()) {
  return mount(WorkflowInspector, {
    props: {
      draft,
      selected,
      fileName: "build-it-our-way.yaml",
      modelLabel: (model: string | null) => (model === "strong" ? "Strong · Opus 5.5" : model ?? "No model"),
      skills: ["fleet-code-review", "fleet-run"],
    },
  });
}

function lastDraft(wrapper: ReturnType<typeof inspect>): WorkflowDraft {
  const updates = wrapper.emitted("update") as [WorkflowDraft][];
  return updates.at(-1)![0];
}

describe("WorkflowInspector", () => {
  it("shows the workflow's settings when no step is selected, with what arrives later greyed out", async () => {
    const wrapper = inspect(null);

    expect(wrapper.text()).toContain("build-it-our-way.yaml");
    expect(wrapper.text()).toContain("A sentence you type");
    expect(wrapper.text()).toContain("Arrives with Stage 2");
    await wrapper.get("[data-testid='workflow-name']").setValue("Ours");
    expect(lastDraft(wrapper).name).toBe("Ours");
    await wrapper.get("#wf-hint").setValue("What should it fix?");
    expect(lastDraft(wrapper).placeholder).toBe("What should it fix?");
  });

  it("renames a step's title without touching its id", async () => {
    const wrapper = inspect(0);

    await wrapper.get("[data-testid='workflow-step-title']").setValue("Outline");

    const draft = lastDraft(wrapper);
    expect(draft.steps[0]).toMatchObject({ id: "plan", title: "Outline" });
    expect(draft.steps[1].choices[1].to).toBe("plan");
  });

  it("changes a step's id on purpose, and every reference follows", async () => {
    const wrapper = inspect(0);

    const id = wrapper.get("[data-testid='workflow-step-id']");
    await id.setValue("outline");
    await id.trigger("change");

    const draft = lastDraft(wrapper);
    expect(draft.steps[0].id).toBe("outline");
    expect(draft.steps[1].choices.map((choice) => choice.to)).toEqual(["implement", "outline"]);
    expect(draft.steps[2].prompt).toContain("{{steps.outline.files}}");
    expect(draft.steps[3].prompt).toContain("{{steps.outline.summary}}");
  });

  it("refuses an id another step has", async () => {
    const wrapper = inspect(0);

    const id = wrapper.get("[data-testid='workflow-step-id']");
    await id.setValue("review");
    await id.trigger("change");

    expect(wrapper.emitted("update")).toBeUndefined();
    expect(wrapper.text()).toContain("Another step has that id.");
  });

  it("edits a loop's max, who finishes the step, and inserts a variable chip", async () => {
    const wrapper = inspect(3);

    await wrapper.get("[data-testid='workflow-outcome-max']").setValue("4");
    expect(lastDraft(wrapper).steps[3].maxLoops).toBe(4);

    await wrapper.get("[data-testid='workflow-finish-agent']").trigger("click");
    expect(lastDraft(wrapper).steps[3]).toMatchObject({ finishAgent: true, finishYou: false });

    const chip = wrapper.findAll(".wf-var").find((button) => button.text() === "{{run.base}}")!;
    await chip.trigger("click");
    expect(lastDraft(wrapper).steps[3].prompt).toContain("{{run.base}}");
  });

  it("says when two loops share the step's max", async () => {
    const draft = reviewDraft();
    draft.steps[3] = { ...draft.steps[3], outcomes: ["pass", "changes", "redo"], routes: { changes: "implement", redo: "plan" } };
    const wrapper = inspect(3, draft);

    expect(wrapper.findAll("[data-testid='workflow-outcome-max']")).toHaveLength(2);
    expect(wrapper.get("[data-testid='workflow-shared-max']").text()).toBe("At most 2 for this step's loops: changes and redo share it.");
  });

  it("points an outcome at another step, and a max goes once there's no loop", async () => {
    const wrapper = inspect(3);

    const route = wrapper.get("[data-testid='workflow-outcome-changes'] select");
    await route.setValue("end");

    expect(lastDraft(wrapper).steps[3]).toMatchObject({ routes: { changes: "end" }, maxLoops: null });
  });

  it("edits a You decide step's question and choices", async () => {
    const wrapper = inspect(1);

    expect(wrapper.text()).toContain("You decide");
    await wrapper.get("#wf-ask").setValue("Go ahead?");
    expect(lastDraft(wrapper).steps[1].ask).toBe("Go ahead?");

    const [, second] = wrapper.findAll("[data-testid='workflow-choice']");
    await second.get("select").setValue("review");
    expect(lastDraft(wrapper).steps[1].choices[1]).toEqual({ label: "Send back", to: "review", note: true });
  });
});
