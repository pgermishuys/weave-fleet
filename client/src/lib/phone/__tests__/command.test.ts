import { describe, expect, it } from "vitest";
import { commandWords } from "../command";

describe("command words", () => {
  it("splits a command where it may wrap, keeping each word (and its trailing space) whole", () => {
    expect(commandWords("dotnet test *")).toEqual(["dotnet ", "test ", "*"]);
    expect(commandWords("dotnet test tests/WeaveFleet.IntegrationTests --filter X").join("")).toBe("dotnet test tests/WeaveFleet.IntegrationTests --filter X");
    expect(commandWords("")).toEqual([]);
  });
});
