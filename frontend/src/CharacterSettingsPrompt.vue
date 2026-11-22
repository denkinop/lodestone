<script setup lang="ts">
defineProps<{ message: string; canOpenSettings: boolean }>()
const emit = defineEmits<{ settings: [] }>()
</script>

<template>
  <section class="paper character-settings-prompt">
    <p role="status">{{ message }}</p>
    <button v-if="canOpenSettings" type="button" class="secondary" @click="emit('settings')">前往设置</button>
  </section>
</template>

<style scoped>
.character-settings-prompt {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 24px 22px 26px;
  color: var(--muted);
  font-size: .86rem;
  line-height: 1.5;
}
.character-settings-prompt button { flex: none; }

@container app-content (max-width: 680px) {
  .character-settings-prompt { padding: 19px 15px; }
}
</style>
