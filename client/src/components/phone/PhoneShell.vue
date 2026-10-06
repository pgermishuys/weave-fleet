<script setup lang="ts">
import "@/assets/phone.css";
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
      <PhoneToastHost />
    </div>
  </div>
</template>
