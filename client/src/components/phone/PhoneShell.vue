<script setup lang="ts">
import "@/assets/phone.css";
import PhoneToastHost from "@/components/phone/PhoneToastHost.vue";
import { usePhoneEnv } from "@/composables/phone/use-phone-env";

/**
 * The frame for Fleet's phone pages (`/pair`, `/phone…`), without the desktop shell's rail and panels. It switches on
 * the native layer (phone.css, the iOS or Android look, the phone's text size, the keyboard and pressed states) and
 * holds the app frame, which follows the visual viewport so nothing hides under the keyboard: the stage the pages
 * draw on, and the overlays sheets and toasts open in.
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
      <PhoneToastHost />
    </div>
  </div>
</template>
