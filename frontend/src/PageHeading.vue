<script setup lang="ts">
defineProps<{ title: string }>()
</script>

<template>
  <header class="page-heading">
    <h1>{{ title }}</h1>
    <slot />
  </header>
</template>

<style scoped>
.page-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-height: 64px;
  margin-bottom: 20px;
}
.page-heading h1 {
  color: var(--navy);
  font-size: 2.2rem;
  font-weight: 800;
  letter-spacing: -.025em;
  line-height: 1.15;
}
@container app-content (max-width: 680px) {
  .page-heading { min-height: 58px; margin-bottom: 16px; }
  .page-heading h1 { font-size: 1.95rem; }
}
</style>
