<script setup lang="ts">
import { icon, type Equipment } from './character'
import UiIcon from './UiIcon.vue'

defineProps<{ label: string; items: Equipment[]; selected: Equipment | null; iconRoot?: string }>()
const emit = defineEmits<{
  reveal: [entry: Equipment, target: HTMLElement, pinned: boolean]
  dismiss: []
}>()

function reveal(event: Event, entry: Equipment, pinned: boolean) {
  if (entry.model && event.currentTarget instanceof HTMLElement) {
    emit('reveal', entry, event.currentTarget, pinned)
  }
}

function hover(event: PointerEvent, entry: Equipment) {
  if (event.pointerType === 'mouse' && matchMedia('(min-width: 681px)').matches) reveal(event, entry, false)
}

function entryLabel(entry: Equipment) {
  return entry.label + ' · ' + (entry.candidates[0]?.name || (entry.model ? '展示模型 ' + entry.model : '空'))
}
</script>

<template>
  <div class="gear-rail" role="group" :aria-label="label">
    <button v-for="entry in items" :key="entry.slot" class="gear-card" type="button"
      :disabled="!entry.model" :aria-label="entryLabel(entry)" :data-slot="entry.slot"
      :aria-expanded="selected === entry" aria-controls="item-detail"
      @pointerenter="hover($event, entry)" @pointerleave="emit('dismiss')"
      @focus="reveal($event, entry, false)" @blur="emit('dismiss')"
      @click="reveal($event, entry, true)">
      <span class="gear-image">
        <img v-if="icon(entry.candidates[0], iconRoot)" :src="icon(entry.candidates[0], iconRoot)" alt="" width="36" height="36">
        <UiIcon v-else name="shirt" :size="17" />
      </span>
    </button>
  </div>
</template>
