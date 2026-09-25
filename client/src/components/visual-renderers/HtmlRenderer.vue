<script setup lang="ts">
interface Props {
  content: string
}

const props = defineProps<Props>()
</script>

<template>
  <div class="html-renderer">
    <iframe
      :srcdoc="props.content"
      sandbox="allow-same-origin"
      class="html-iframe"
    />
  </div>
</template>

<style scoped>
.html-renderer {
  width: 100%;
  height: 100%;
}

.html-iframe {
  width: 100%;
  height: 100%;
  border: none;
  /* Pages without a background of their own expect a white one. */
  background: white;
  /* Same as the browser canvas: keep Fleet's dark scheme off the frame, or
     a dark page shows its light text on the white above. */
  color-scheme: light;
}
</style>
