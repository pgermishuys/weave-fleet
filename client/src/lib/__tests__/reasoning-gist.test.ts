import { describe, expect, it } from "vitest";
import { reasoningGist } from "@/lib/reasoning-gist";

const THINKING = [
  "The endpoint loads every order and sorts in memory. That's why it grows with the account.",
  "",
  "A cursor on (created_at, id) uses the existing index. Offset paging would scan skipped rows.",
].join("\n");

describe("reasoningGist", () => {
  it("is the first sentence of a finished block", () => {
    expect(reasoningGist(THINKING)).toBe("The endpoint loads every order and sorts in memory.");
  });

  it("prefers the summary the harness sent", () => {
    expect(reasoningGist(THINKING, "**Choosing a paging scheme**\n\nMore detail.")).toBe("**Choosing a paging scheme**");
  });

  it("follows the newest sentence while the model is still writing", () => {
    expect(reasoningGist(THINKING, "Ignored while live", true)).toBe("Offset paging would scan skipped rows.");
    expect(reasoningGist("First thought. Now I am wri", undefined, true)).toBe("Now I am wri");
  });

  it("takes Markdown line markers off", () => {
    expect(reasoningGist("## Planning\n\nRest")).toBe("Planning");
    expect(reasoningGist("- check the index\n- write tests")).toBe("check the index");
    expect(reasoningGist("> quoted line")).toBe("quoted line");
  });

  it("is empty for an empty block and cut short for a very long one", () => {
    expect(reasoningGist("  \n ")).toBe("");
    const long = reasoningGist("x".repeat(500));
    expect(long).toHaveLength(241);
    expect(long.endsWith("…")).toBe(true);
  });
});
