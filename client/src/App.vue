<script setup lang="ts">
import { RouterProvider } from "@tanstack/vue-router";
import { useUnsavedFilesGuard } from "@/composables/use-unsaved-files-guard";
import { router } from "./router";

useUnsavedFilesGuard();
</script>

<template>
  <RouterProvider :router="router" />
</template>
