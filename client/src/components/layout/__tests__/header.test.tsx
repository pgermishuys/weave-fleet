/**
 * @vitest-environment jsdom
 */

import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Header } from "@/components/layout/header";

const mockUseSidebar = vi.fn(() => ({
  isMobileNav: false,
  mobileDrawerOpen: false,
  setMobileDrawerOpen: vi.fn(),
}));

const mockUseAppShell = vi.fn();

vi.mock("@/contexts/sidebar-context", () => ({
  useSidebar: () => mockUseSidebar(),
}));

vi.mock("@/contexts/app-shell-context", () => ({
  useAppShell: () => mockUseAppShell(),
}));

describe("Header", () => {
  it("shows the current user menu when auth is enabled", () => {
    mockUseAppShell.mockReturnValue({
      clientConfig: { authEnabled: true },
      currentUser: { displayName: "Jane Doe", email: "jane@example.com" },
    });

    render(<Header title="Dashboard" />);

    expect(screen.getByRole("button", { name: "Current user menu" })).toBeTruthy();
    expect(screen.getByText("Jane Doe")).toBeTruthy();
  });

  it("hides the current user menu when auth is disabled", () => {
    mockUseAppShell.mockReturnValue({
      clientConfig: { authEnabled: false },
      currentUser: { displayName: "Local User", email: "local@example.com" },
    });

    render(<Header title="Dashboard" />);

    expect(screen.queryByRole("button", { name: "Current user menu" })).toBeNull();
    expect(screen.queryByText("Local User")).toBeNull();
  });
});
