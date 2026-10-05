import { computed, defineComponent } from "vue";
import { Outlet, createRootRoute, useLocation } from "@tanstack/vue-router";
import AuthGate from "@/components/auth/AuthGate.vue";
import AppShell from "@/components/layout/AppShell.vue";
import NotFoundPage from "@/components/pages/NotFoundPage.vue";
import PhoneShell from "@/components/phone/PhoneShell.vue";

/**
 * Root layout component.
 *
 * Uses `defineComponent` with a `setup` function so that reactive
 * dependencies (`useLocation`) are tracked in setup — not inside the
 * render function.  This prevents Vue from recreating the slot
 * closures on every navigation, which previously caused `AppShell`
 * (and its children like `ContextPanel`) to unmount and remount,
 * leaving the sidebar blank until a hard refresh.
 */
const RootLayout = defineComponent({
  name: "RootLayout",
  setup() {
    const pathname = useLocation({
      select: (location) => location.pathname,
    });

    const isLoginRoute = computed(() => pathname.value === "/login");
    // Pairing happens before the phone has any key, so it skips the sign-in gate and the desktop shell.
    const isPairRoute = computed(() => pathname.value === "/pair");

    // Stable slot functions — created once in setup, not on every render.
    const authGateSlots = { default: () => <Outlet /> };
    const appShellSlots = {
      default: () => <AuthGate v-slots={authGateSlots} />,
    };
    const phoneShellSlots = { default: () => <Outlet /> };

    return () => {
      if (isLoginRoute.value) {
        return <Outlet />;
      }

      if (isPairRoute.value) {
        return <PhoneShell v-slots={phoneShellSlots} />;
      }

      return <AppShell v-slots={appShellSlots} />;
    };
  },
});

export const Route = createRootRoute({
  component: RootLayout,
  notFoundComponent: NotFoundPage,
});
