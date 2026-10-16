import { describe, expect, it } from "vitest";
import type { ModelOption } from "@/composables/use-models";
import { createModelSelectionKey } from "@/composables/use-models";
import { groupModels } from "@/lib/model-picker-groups";
import { modelPickerKey } from "@/stores/model-picker";

function model(provider: string, id: string): ModelOption {
  return { id, name: id, providerId: provider, selectionKey: createModelSelectionKey(provider, id), provider: provider.toUpperCase(), description: "" };
}
const key = (provider: string, id: string, harness = "opencode") => modelPickerKey(harness, provider, id);
const ids = (items: ModelOption[]) => items.map((item) => item.id);

const few = [model("a", "m1"), model("a", "m2"), model("b", "m3")];
const many = [...Array.from({ length: 6 }, (_, i) => model("a", `a${i}`)), ...Array.from({ length: 4 }, (_, i) => model("b", `b${i}`))];

describe("groupModels", () => {
  it("looks like today's list when nothing is starred or recent", () => {
    expect(groupModels(few, "opencode", [], [], true)).toEqual([{ title: undefined, level: "group", items: few }]);
  });

  it("lists favourites in starred order and keeps them in All models", () => {
    const sections = groupModels(few, "opencode", [key("b", "m3"), key("a", "m1")], [], true);
    expect(sections.map((s) => s.title)).toEqual(["Favourites", "All models"]);
    expect(ids(sections[0]!.items)).toEqual(["m3", "m1"]);
    expect(ids(sections[1]!.items)).toEqual(["m1", "m2", "m3"]);
  });

  it("ignores keys the list doesn't have", () => {
    const sections = groupModels(few, "opencode", [key("gone", "x")], [key("gone", "y")], true);
    expect(sections).toHaveLength(1);
  });

  it("lists this harness's recent minus favourites, at most five", () => {
    const list = Array.from({ length: 8 }, (_, i) => model("a", `m${i}`));
    const recent = [...list.map((m) => key("a", m.id)), key("a", "m0", "claude")];
    const sections = groupModels(list, "opencode", [key("a", "m0")], recent, true);
    expect(ids(sections.find((s) => s.title === "Recent")!.items)).toEqual(["m1", "m2", "m3", "m4", "m5"]);
    expect(groupModels(list, "claude", [], recent, true)[0]!.title).toBe("Recent");
  });

  it("leaves Recent out when asked", () => {
    expect(groupModels(few, "opencode", [], [key("a", "m1")], false).map((s) => s.title)).toEqual([undefined]);
  });

  it("splits by provider only past eight models from several providers", () => {
    const sections = groupModels(many, "opencode", [], [], true);
    expect(sections.map((s) => [s.title, s.level])).toEqual([["A", "provider"], ["B", "provider"]]);
    expect(groupModels(many.filter((m) => m.provider === "A"), "opencode", [], [], true)).toHaveLength(1);
  });

  it("puts All models above the provider headers when something is above it", () => {
    const sections = groupModels(many, "opencode", [key("b", "b1")], [], true);
    expect(sections.map((s) => s.title)).toEqual(["Favourites", "All models", "A", "B"]);
  });
});
