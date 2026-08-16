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
  background: white;
}
</style>
