import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it } from "vitest";
import { createPinia } from "pinia";
import { nextTick } from "vue";
import ConversationPage from "@/components/session/ConversationPage.vue";
import MessageBubble from "@/components/session/MessageBubble.vue";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";

const pageTool: ToolCardItem = {
  id: "part-page",
  title: "CI test times",
  kind: "fleet_page_show",
  status: "Completed",
  output: "Showing \"CI test times\" in the conversation, above your reply.",
  initiallyCollapsed: true,
  page: { path: "/pages/pg_0123456789abcdef0123456789abcdef/times.html", id: "pg_0123456789abcdef0123456789abcdef", source: "/tmp/ci/times.html" },
};

let wrapper: { unmount(): void } | undefined;

function bubble(tools: ToolCardItem[]) {
  const mounted = mount(MessageBubble, {
    attachTo: document.body,
    global: { plugins: [createPinia()], stubs: { teleport: false } },
    props: { author: "Agent", role: "assistant", body: "", tools, showIdentity: true, clusterPosition: "single" },
  });
  wrapper = mounted;
  return mounted;
}

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
});

describe("ConversationPage", () => {
  it("offers to open the page in a new tab, and does not when it is contained", () => {
    const view = bubble([pageTool]);
    expect(view.find("[aria-label='Open in a new tab']").exists()).toBe(true);
    expect(view.get("iframe").attributes("sandbox")).toBe(
      "allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads",
    );

    const contained = mount(ConversationPage, {
      props: { page: pageTool.page!, title: "Mod page", src: "http://fleet.test/p.html", contained: true },
    });
    expect(contained.find("[aria-label='Open in a new tab']").exists()).toBe(false);
    expect(contained.find("[data-testid='conversation-page-expand']").exists()).toBe(true);
    expect(contained.get("iframe").attributes("sandbox")).toBe("allow-scripts allow-forms allow-modals allow-downloads");
    contained.unmount();
  });

  it("shows the page under the calls, outside their box, named with Fleet's theme", () => {
    const view = bubble([pageTool]);

    const page = view.get("[data-testid='conversation-page']");
    expect(view.find(".msg-tools [data-testid='conversation-page']").exists()).toBe(false);
    const frame = page.get("iframe");
    expect(frame.attributes("src")).toContain("/pages/pg_0123456789abcdef0123456789abcdef/times.html");
    expect(frame.attributes("sandbox")).not.toContain("allow-same-origin");
    const named = JSON.parse(frame.attributes("name")!.slice("fleet:".length));
    expect(named.place).toBe("conversation");
    expect(named.variables["--fleet-chart-1"]).toBeTruthy();
  });

  it("grows its frame to the height the page reports, from its own frame only", async () => {
    const view = bubble([pageTool]);
    const frame = view.get("iframe").element as HTMLIFrameElement;
    const size = (height: number) => ({ jsonrpc: "2.0", method: "ui/notifications/size-changed", params: { height } });

    window.dispatchEvent(new MessageEvent("message", { data: size(480), origin: "null", source: window }));
    await nextTick();
    expect(frame.style.height).toBe("120px");

    window.dispatchEvent(new MessageEvent("message", { data: size(480), origin: "null", source: frame.contentWindow }));
    await nextTick();
    expect(frame.style.height).toBe("480px");
  });

  it("opens full size over the app, and Escape puts it away", async () => {
    const view = bubble([pageTool]);

    await view.get("[data-testid='conversation-page-expand']").trigger("click");
    expect(document.body.querySelector("[data-testid='conversation-page-full'] iframe")).not.toBeNull();

    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    await nextTick();
    expect(document.body.querySelector("[data-testid='conversation-page-full']")).toBeNull();
  });

  it("leaves calls without a page as they were", () => {
    const view = bubble([{ ...pageTool, page: undefined }]);

    expect(view.find("[data-testid='conversation-page']").exists()).toBe(false);
  });
});
