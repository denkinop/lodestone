<script setup lang="ts">
import { computed, ref } from 'vue'
import type { DatabaseMapLocation } from './bridge'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ location: DatabaseMapLocation }>()
const expanded = ref(false)
const mapParameters = computed(() => {
  const { id, x, y } = props.location
  const point = x != null && y != null
  const params = new URLSearchParams({ f: point ? 'mark' : 'area', id: String(id) })
  if (point) {
    params.set('x', String(x))
    params.set('y', String(y))
  }
  return params.toString()
})
const mapUrl = computed(() => 'https://map.ffcafe.cn/#' + mapParameters.value)
const previewUrl = computed(() => import.meta.env.BASE_URL + 'map-preview/index.html#' + mapParameters.value)

function updateExpanded(event: Event) {
  expanded.value = (event.currentTarget as HTMLDetailsElement).open
}
</script>

<template>
  <details class="database-map" @toggle="updateExpanded">
    <summary class="database-map-heading">
      <span class="database-map-icon"><UiIcon name="location" :size="19" /></span>
      <span><strong>{{ location.x == null || location.y == null ? '区域地图' : '查看地图' }}</strong><small>{{ location.name }}</small></span>
      <UiIcon class="database-map-disclosure" name="right" :size="17" />
    </summary>
    <div v-if="expanded" class="database-map-body">
      <iframe :key="previewUrl" :src="previewUrl" :title="location.name + '互动地图'" referrerpolicy="no-referrer"
        sandbox="allow-scripts"></iframe>
      <a :href="mapUrl" target="_blank" rel="noopener noreferrer">
        在浏览器中打开 <UiIcon name="external" :size="15" />
      </a>
    </div>
  </details>
</template>

<style scoped>
.database-map {
  margin-top: 16px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: 14px;
  background: var(--surface);
}
.database-map-heading {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 54px;
  padding: 9px 12px;
  list-style: none;
  color: var(--navy);
  cursor: pointer;
}
.database-map-heading::-webkit-details-marker { display: none; }
.database-map-heading:focus-visible { outline: 2px solid var(--blue); outline-offset: -2px; }
.database-map-heading strong, .database-map-heading small { display: block; }
.database-map-heading small { margin-top: 2px; color: var(--muted); }
.database-map-icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 10px;
  background: var(--blue-light);
  color: var(--blue);
}
.database-map-disclosure { margin-inline-start: auto; color: var(--blue); }
.database-map[open] .database-map-disclosure { transform: rotate(90deg); }
.database-map-body {
  display: grid;
  gap: 6px;
  padding: 0 10px 7px;
}
.database-map-body iframe {
  display: block;
  width: 100%;
  height: clamp(290px, 56dvh, 610px);
  min-height: 290px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--paper);
}
.database-map-body a {
  display: inline-flex;
  align-items: center;
  justify-self: end;
  gap: 6px;
  padding: 5px;
  color: var(--blue);
  font-size: .84rem;
  font-weight: 650;
  text-decoration: none;
}
.database-map-body a:hover { text-decoration: underline; }
@container app-content (max-width: 560px) {
  .database-map-body iframe { height: auto; aspect-ratio: 1.12; }
}
</style>
