import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import QueuedMessages from "@/components/session/QueuedMessages.vue";
import type { QueuedMessage } from "@/composables/use-session-queue";

const items: QueuedMessage[] = [
  { id: "queue-1", text: "also update the changelog", kind: "prompt" },
  { id: "queue-2", text: "/review", kind: "command" },
];

describe("QueuedMessages", () => {
  it("lists what waits for the turn to end, next one first", () => {
    const wrapper = mount(QueuedMessages, { props: { items, canSendNow: () => true } });

    const rows = wrapper.findAll("[data-testid='queued-message']");
    expect(rows.map((row) => row.text())).toEqual([
      expect.stringContaining("also update the changelog"),
      expect.stringContaining("/review"),
    ]);
    expect(rows[0]!.text()).toContain("Next");
  });

  it("offers Send now only for what can go into the turn", async () => {
    const wrapper = mount(QueuedMessages, {
      props: { items, canSendNow: (item: QueuedMessage) => !item.text.startsWith("/") },
    });

    const sendNow = wrapper.findAll("[data-testid='queued-send-now']");
    expect(sendNow).toHaveLength(1);
    await sendNow[0]!.trigger("click");
    expect(wrapper.emitted("sendNow")).toEqual([[0]]);
  });

  it("has no Send now when the session's harness can't take a message mid-turn", () => {
    const wrapper = mount(QueuedMessages, { props: { items, canSendNow: () => false } });

    expect(wrapper.find("[data-testid='queued-send-now']").exists()).toBe(false);
    expect(wrapper.findAll("[data-testid='queued-remove']")).toHaveLength(2);
  });

  it("takes a message back out of the queue", async () => {
    const wrapper = mount(QueuedMessages, { props: { items, canSendNow: () => false } });

    await wrapper.findAll("[data-testid='queued-remove']")[1]!.trigger("click");
    expect(wrapper.emitted("remove")).toEqual([[1]]);
  });
});
