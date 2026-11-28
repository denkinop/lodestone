<script setup lang="ts">
import { computed, ref } from 'vue'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ icon: number; fallback?: string }>()
const standardResolution = ref<number>()
const failed = ref(0)
const source = computed(() => {
  if (props.icon <= 0 || failed.value === props.icon) return
  const group = String(Math.floor(props.icon / 1000) * 1000).padStart(6, '0')
  const file = String(props.icon).padStart(6, '0')
  const suffix = standardResolution.value === props.icon ? '' : '_hr1'
  return 'https://xivapi-v2.xivcdn.com/api/asset?path=' + encodeURIComponent(`ui/icon/${group}/${file}${suffix}.tex`) + '&format=webp'
})

function useFallback() {
  if (standardResolution.value === props.icon) failed.value = props.icon
  else standardResolution.value = props.icon
}
</script>

<template>
  <span class="item-icon" aria-hidden="true">
    <img v-if="source" :key="source" :src="source" alt="" width="48" height="48" loading="lazy"
      referrerpolicy="no-referrer" @error="useFallback">
    <UiIcon v-else :name="fallback || 'cube'" :size="21" />
  </span>
</template>

<style scoped>
.item-icon {
  display: grid;
  place-items: center;
  width: 48px;
  height: 48px;
  border-radius: 12px;
  overflow: hidden;
  background: var(--blue-light);
  color: var(--blue);
}
.item-icon img { display: block; width: 100%; height: 100%; object-fit: contain; }
</style>
