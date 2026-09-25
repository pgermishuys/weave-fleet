import { describe, expect, it } from "vitest";
import { appAddress, navMessage, readBridgeMessage } from "@/lib/preview-bridge";

describe("preview bridge v1", () => {
  it("reads the page's hello, location and update", () => {
    expect(readBridgeMessage({ fleet: 1, type: "hello", hmr: "vite", href: "http://p:1/", title: "Shop" }))
      .toEqual({ type: "hello", hmr: "vite", href: "http://p:1/", title: "Shop" });
    expect(readBridgeMessage({ fleet: 1, type: "location", href: "http://p:1/cart" }))
      .toEqual({ type: "location", href: "http://p:1/cart", title: "" });
    expect(readBridgeMessage({ fleet: 1, type: "update" })).toEqual({ type: "update" });
  });

  it("reads a link the page wants opened, in this tab unless it says a new one", () => {
    expect(readBridgeMessage({ fleet: 1, type: "open", href: "http://localhost:5199/", newTab: true }))
      .toEqual({ type: "open", href: "http://localhost:5199/", newTab: true });
    expect(readBridgeMessage({ fleet: 1, type: "open", href: "http://localhost:5199/" }))
      .toEqual({ type: "open", href: "http://localhost:5199/", newTab: false });
    expect(readBridgeMessage({ fleet: 1, type: "open", newTab: true })).toBeNull();
  });

  it("takes a hot-reload client it doesn't know as none", () => {
    expect(readBridgeMessage({ fleet: 1, type: "hello", hmr: "parcel", href: "http://p:1/" })).toMatchObject({ hmr: "none" });
  });

  it("ignores other versions, types and senders", () => {
    expect(readBridgeMessage({ fleet: 2, type: "update" })).toBeNull();
    expect(readBridgeMessage({ fleet: 1, type: "error", message: "boom" })).toBeNull();
    expect(readBridgeMessage({ type: "fleet-browser:location", href: "http://p:1/" })).toBeNull();
    expect(readBridgeMessage({ fleet: 1, type: "location" })).toBeNull();
    expect(readBridgeMessage("webpackHotUpdate")).toBeNull();
    expect(readBridgeMessage(null)).toBeNull();
  });

  it("sends navigation as v1", () => {
    expect(navMessage("back")).toEqual({ fleet: 1, type: "nav", action: "back" });
  });

  it("shows the app's own address for the preview's page", () => {
    expect(appAddress("http://p1.localhost:41234/cart?x=1#top", "http://p1.localhost:41234", "http://localhost:5173"))
      .toBe("http://localhost:5173/cart?x=1#top");
    expect(appAddress("about:blank", "http://p1.localhost:41234", "http://localhost:5173")).toBe("about:blank");
  });
});
