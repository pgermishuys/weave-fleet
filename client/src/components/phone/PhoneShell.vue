<script setup lang="ts">
import "@/assets/phone.css";
import ModsSafeModeBanner from "@/components/mods/ModsSafeModeBanner.vue";
import PhoneToastHost from "@/components/phone/PhoneToastHost.vue";
import { usePhoneEnv } from "@/composables/phone/use-phone-env";

/**
 * The frame for Fleet's phone pages (`/pair`, `/phone…`): the window chrome the pages' panels float on, as the
 * desktop's do, without the desktop shell's rail. It switches on the phone layer (phone.css: one Fleet design on every
 * phone, its type scale, the keyboard and pressed states) and holds the app frame, which follows the visual viewport
 * so nothing hides under the keyboard: the stage the pages draw on, and the overlays sheets and toasts open in.
 */
usePhoneEnv();
</script>

<template>
  <div
    class="phone-shell ph-app"
    data-testid="phone-shell"
  >
    <!-- In flow: it pushes the page down while mods are stopped, and takes no room otherwise. -->
    <div class="ph-mods-banner">
      <ModsSafeModeBanner phone />
    </div>
    <div class="ph-stage">
      <slot />
    </div>
    <div
      id="ph-overlays"
      class="ph-overlays"
    >
      <PhoneToastHost />
    </div>
  </div>
</template>

<style scoped>
/* A column, so the banner above the stage makes room instead of covering the page's header. */
.phone-shell {
  display: flex;
  flex-direction: column;
}

.phone-shell .ph-stage {
  position: relative;
  inset: auto;
  min-height: 0;
  flex: 1;
}

.ph-mods-banner {
  flex-shrink: 0;
  padding-top: env(safe-area-inset-top);
  border-bottom: 1px solid var(--border);
  background: var(--ph-chrome);
}

.ph-mods-banner:empty {
  display: none;
}
</style>
