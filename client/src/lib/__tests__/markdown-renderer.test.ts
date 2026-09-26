import { describe, expect, it } from "vitest";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";

describe("createMarkdownRenderer", () => {
  it("draws GitHub task lists as checklist items", () => {
    const html = createMarkdownRenderer().render("## Test plan\n\n- [x] unit tests\n- [ ] live on a scratch Fleet\n- plain item\n");

    expect(html).toContain('<li class="task-list-item task-list-item--done">unit tests</li>');
    expect(html).toContain('<li class="task-list-item">live on a scratch Fleet</li>');
    expect(html).toContain("<li>plain item</li>");
    expect(html).not.toContain("[x]");
  });

  it("leaves brackets that aren't task markers alone", () => {
    const html = createMarkdownRenderer().render("- [link](https://example.com) here\n- [x]no space\n\n[x] outside a list\n");

    expect(html).not.toContain("task-list-item");
    expect(html).toContain("[x]no space");
    expect(html).toContain("[x] outside a list");
  });
});
