<script setup lang="ts">
import { RouterProvider } from "@tanstack/vue-router";
import { router } from "./router";
</script>

<template>
  <RouterProvider :router="router" />
</template>
