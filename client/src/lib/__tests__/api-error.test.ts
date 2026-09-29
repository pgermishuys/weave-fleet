import { describe, expect, it } from "vitest";
import { extractApiError } from "@/lib/api-error";

describe("extractApiError", () => {
  it("reads Fleet's { error } body", () => {
    expect(extractApiError({ error: 'Unknown field "prompt".' }, "Failed")).toBe('Unknown field "prompt".');
  });

  it("prefers a problem's detail to its title", () => {
    expect(extractApiError({ title: "Bad Request", detail: "Name is required." }, "Failed")).toBe("Name is required.");
  });

  it("falls back when there's nothing to read", () => {
    expect(extractApiError({}, "Failed")).toBe("Failed");
    expect(extractApiError(undefined, "Failed")).toBe("Failed");
  });
});
