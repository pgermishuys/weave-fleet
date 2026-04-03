"use client";

import { Suspense } from "react";
import {
  useSidebar,
  SIDEBAR_RAIL_WIDTH,
} from "@/contexts/sidebar-context";
import { SidebarIconRail } from "@/components/layout/sidebar-icon-rail";
import { ContextualPanel } from "@/components/layout/sidebar-panel";
import {
  Sheet,
  SheetContent,
  SheetTitle,
} from "@/components/ui/sheet";

export function Sidebar() {
  const { panelOpen, width, isMobileNav, mobileDrawerOpen, setMobileDrawerOpen } = useSidebar();
  const totalWidth = panelOpen ? SIDEBAR_RAIL_WIDTH + width : SIDEBAR_RAIL_WIDTH;

  // Mobile: render sidebar as a Sheet drawer
  if (isMobileNav) {
    return (
      <Sheet open={mobileDrawerOpen} onOpenChange={setMobileDrawerOpen}>
        <SheetContent
          side="left"
          showCloseButton={false}
          className="p-0 w-[280px] bg-sidebar border-sidebar-border flex flex-row gap-0"
        >
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <Suspense><SidebarIconRail /></Suspense>
          <Suspense><ContextualPanel /></Suspense>
        </SheetContent>
      </Sheet>
    );
  }

  // Desktop: render inline aside as before
  return (
    <aside
      className="relative flex h-screen flex-row border-r border-sidebar-border bg-sidebar overflow-hidden"
      style={{ width: totalWidth }}
    >
      <Suspense><SidebarIconRail /></Suspense>
      {panelOpen && <Suspense><ContextualPanel /></Suspense>}
    </aside>
  );
}
