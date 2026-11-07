import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import MessageBubble from "@/components/session/MessageBubble.vue";

type Position = "single" | "first" | "middle" | "last";

function bubble(role: "user" | "assistant", clusterPosition: Position) {
  return mount(MessageBubble, {
    props: {
      author: "Agent",
      role,
      modelName: "Model One",
      createdAt: Date.now() - 120_000,
      body: "Hello there",
      showIdentity: true,
      clusterPosition,
    },
  });
}

// The pill hangs 20px below its message, but messages of one cluster sit 6px apart: on
// a cluster's first and middle messages it would land on the next message's first line.
describe("MessageBubble model and time pill", () => {
  it.each<Position>(["single", "last"])("shows on an assistant message that ends its group (%s)", (position) => {
    expect(bubble("assistant", position).find(".msg-meta").exists()).toBe(true);
  });

  it.each<Position>(["first", "middle"])("is left off an assistant message another follows closely (%s)", (position) => {
    expect(bubble("assistant", position).find(".msg-meta").exists()).toBe(false);
  });

  it.each<Position>(["first", "middle"])("stays beside your own clustered messages (%s)", (position) => {
    expect(bubble("user", position).find(".msg-meta").exists()).toBe(true);
  });
});
