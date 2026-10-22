import { beforeEach, describe, expect, it, vi } from "vitest";

const post = vi.fn();
vi.mock("@/lib/machine-target", () => ({ useMachineTarget: () => ({ api: { POST: post } }) }));

import { useQuestionAnswer } from "@/composables/use-question-answer";

function reply(status: number, statusText: string) {
  return { error: status >= 400 ? {} : undefined, response: new Response(null, { status, statusText }) };
}

describe("useQuestionAnswer", () => {
  beforeEach(() => post.mockReset());

  it.each([404, 409])("says a question is already answered when Fleet answers %i (it was answered from another device)", async (status) => {
    post.mockResolvedValue(reply(status, "Not Found"));
    const { answerQuestion, rejectQuestion } = useQuestionAnswer("s1");

    await expect(answerQuestion("q1", [["Yes"]])).rejects.toThrow("This question was already answered.");
    await expect(rejectQuestion("q1")).rejects.toThrow("This question was already answered.");
  });

  it("keeps the status for other failures", async () => {
    post.mockResolvedValue(reply(500, "Internal Server Error"));
    await expect(useQuestionAnswer("s1").answerQuestion("q1", [["Yes"]])).rejects.toThrow("Failed to answer question: Internal Server Error");
  });
});
