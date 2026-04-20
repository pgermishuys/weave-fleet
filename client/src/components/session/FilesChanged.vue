<script setup lang="ts">
interface ChangedFile {
  path: string;
  additions: number;
  deletions: number;
}

defineProps<{
  files: readonly ChangedFile[];
}>();
</script>

<template>
  <section
    class="files-changed"
    aria-label="Files changed"
  >
    <div class="files-changed__header">
      <p class="files-changed__title">
        Files changed
      </p>
      <p class="files-changed__count">
        {{ files.length }} files
      </p>
    </div>

    <ul class="files-changed__list">
      <li
        v-for="file in files"
        :key="file.path"
        class="files-changed__item"
      >
        <div class="files-changed__path-group">
          <span class="files-changed__path">{{ file.path }}</span>
        </div>

        <div
          class="files-changed__stats"
          aria-label="File diff summary"
        >
          <span class="files-changed__stat files-changed__stat--add">+{{ file.additions }}</span>
          <span class="files-changed__stat files-changed__stat--remove">-{{ file.deletions }}</span>
        </div>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.files-changed {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.files-changed__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.files-changed__title,
.files-changed__count {
  margin: 0;
  font-size: 12px;
}

.files-changed__title {
  font-weight: 600;
  color: var(--text);
}

.files-changed__count {
  color: var(--muted);
}

.files-changed__list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.files-changed__item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-width: 0;
  padding: 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.02);
}

.files-changed__path-group {
  min-width: 0;
  flex: 1;
}

.files-changed__path {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--text);
}

.files-changed__stats {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.files-changed__stat {
  font-size: 11px;
  font-weight: 600;
}

.files-changed__stat--add {
  color: var(--running);
}

.files-changed__stat--remove {
  color: var(--error);
}
</style>
