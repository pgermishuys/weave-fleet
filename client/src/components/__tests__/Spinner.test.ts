import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import { Spinner } from "@/components/ui/spinner";

describe("Spinner", () => {
  it.each([
    ["xs", "12"],
    ["sm", "14"],
    ["md", "16"],
    ["lg", "20"],
  ] as const)("draws %s at %spx", (size, px) => {
    const svg = mount(Spinner, { props: { size } });
    expect(svg.attributes("width")).toBe(px);
    expect(svg.attributes("height")).toBe(px);
  });

  it("turns only when motion is welcome, and fades instead under prefers-reduced-motion", () => {
    const classes = mount(Spinner).classes();
    expect(classes).toContain("motion-safe:animate-spin");
    expect(classes).toContain("motion-reduce:animate-pulse");
    expect(classes).not.toContain("animate-spin");
  });

  it("is hidden from screen readers unless it says what is loading", () => {
    expect(mount(Spinner).attributes("aria-hidden")).toBe("true");
    const labelled = mount(Spinner, { props: { label: "Loading skills" } });
    expect(labelled.attributes("role")).toBe("status");
    expect(labelled.attributes("aria-label")).toBe("Loading skills");
    expect(labelled.attributes("aria-hidden")).toBeUndefined();
  });
});
