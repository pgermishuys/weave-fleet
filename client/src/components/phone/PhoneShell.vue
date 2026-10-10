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
    <div class="ph-stage">
      <slot />
    </div>
    <div
      id="ph-overlays"
      class="ph-overlays"
    >
      <!-- Floats over the top of the page, and takes no room unless mods are stopped. -->
      <div class="ph-mods-banner">
        <ModsSafeModeBanner />
      </div>
      <PhoneToastHost />
    </div>
  </div>
</template>

<style scoped>
.ph-mods-banner {
  position: absolute;
  top: max(8px, env(safe-area-inset-top));
  right: 8px;
  left: 8px;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: 12px;
  pointer-events: auto;
}

.ph-mods-banner:empty {
  display: none;
}
</style>
