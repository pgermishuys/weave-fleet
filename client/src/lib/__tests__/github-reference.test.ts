import { describe, expect, it } from "vitest";
import { findHashTrigger, parseGitHubRemote, removeTrigger } from "@/lib/github-reference";

describe("github-reference", () => {
  it("finds # at the start or after a space, up to the caret", () => {
    expect(findHashTrigger("#", 1)).toEqual({ start: 0, end: 1, query: "" });
    expect(findHashTrigger("Fix #31", 7)).toEqual({ start: 4, end: 7, query: "31" });
    expect(findHashTrigger("Fix #flick and more", 10)).toEqual({ start: 4, end: 10, query: "flick" });
  });

  it("ignores # inside a word, after a space was typed, or away from the caret", () => {
    expect(findHashTrigger("C#", 2)).toBeNull();
    expect(findHashTrigger("issue#3", 7)).toBeNull();
    expect(findHashTrigger("Fix #31 now", 11)).toBeNull();
    expect(findHashTrigger("Fix #31", null)).toBeNull();
  });

  it("reads the repository from a GitHub remote", () => {
    expect(parseGitHubRemote("git@github.com:acme/rocket.git")).toEqual({ owner: "acme", repo: "rocket" });
    expect(parseGitHubRemote("https://github.com/acme/rocket")).toEqual({ owner: "acme", repo: "rocket" });
    expect(parseGitHubRemote("https://github.com/acme/rocket.js.git/")).toEqual({ owner: "acme", repo: "rocket.js" });
    expect(parseGitHubRemote("https://gitlab.com/acme/rocket.git")).toBeNull();
    expect(parseGitHubRemote(null)).toBeNull();
  });

  it("drops the #query without leaving two spaces", () => {
    expect(removeTrigger("Fix #31", { start: 4, end: 7, query: "31" })).toEqual({ text: "Fix ", caret: 4 });
    expect(removeTrigger("Fix #31 today", { start: 4, end: 7, query: "31" })).toEqual({ text: "Fix today", caret: 4 });
    expect(removeTrigger("#31 please", { start: 0, end: 3, query: "31" })).toEqual({ text: "please", caret: 0 });
  });
});
