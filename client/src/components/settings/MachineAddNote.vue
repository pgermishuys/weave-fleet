<script setup lang="ts">
/** What adding a machine means, in three points. Shown under a found machine and while one is being set up. */
defineProps<{ name: string }>();
</script>

<template>
  <ul
    class="m-0 grid list-disc gap-1 pl-5 text-xs text-muted"
    data-testid="machines-add-note"
  >
    <li>Sessions you start on {{ name }} run there, with {{ name }}'s sign-ins, folders and agents.</li>
    <li>This Fleet can run agents, change files and open terminals on {{ name }}.</li>
    <li>{{ name }} gives this Fleet a key of its own. Forget {{ name }} here to stop.</li>
  </ul>
</template>
