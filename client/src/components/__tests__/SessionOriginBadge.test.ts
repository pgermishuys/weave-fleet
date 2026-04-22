import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionOriginBadge from "@/components/SessionOriginBadge.vue";
import type { SessionOrigin } from "@/lib/api-types";

function createOrigin(overrides: Partial<SessionOrigin> = {}): SessionOrigin {
  return {
    sourceType: "github_issue",
    title: "Issue #42",
    resourceUrl: "https://github.com/acme/app/issues/42",
    resourceId: "42",
    providerId: "builtin.github",
    ...overrides,
  };
}

describe("SessionOriginBadge", () => {
  it("renders a GitHub issue origin link", () => {
    const wrapper = mount(SessionOriginBadge, {
      props: {
        origin: createOrigin(),
      },
    });

    expect(wrapper.get("a").attributes("href")).toBe("https://github.com/acme/app/issues/42");
    expect(wrapper.text()).toContain("Issue #42");
    expect(wrapper.find("svg").exists()).toBe(true);
  });

  it("renders a GitHub pull request origin link", () => {
    const wrapper = mount(SessionOriginBadge, {
      props: {
        origin: createOrigin({
          sourceType: "github_pull_request",
          title: "PR #73",
          resourceUrl: "https://github.com/acme/app/pull/73",
          resourceId: "73",
        }),
      },
    });

    expect(wrapper.get("a").attributes("href")).toBe("https://github.com/acme/app/pull/73");
    expect(wrapper.text()).toContain("PR #73");
    expect(wrapper.find("svg").exists()).toBe(true);
  });

  it("renders nothing when origin is null", () => {
    const wrapper = mount(SessionOriginBadge, {
      props: {
        origin: null,
      },
    });

    expect(wrapper.find("a").exists()).toBe(false);
    expect(wrapper.text()).toBe("");
  });
});
