<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { icon, wikiUrl, type Character, type Equipment } from './demo'
import EquipmentRail from './EquipmentRail.vue'
import JobLevels from './JobLevels.vue'
import PreviewStage from './PreviewStage.vue'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ character: Character }>()
const selected = ref<Equipment | null>(null)
const pinned = ref(false)
const candidate = ref(0)
const position = ref({ x: 0, y: 0 })
let closeTimer: ReturnType<typeof setTimeout> | undefined

const fields = computed(() => [
  ['种族', props.character.race], ['部族', props.character.tribe], ['性别', props.character.gender],
  ['所属服务器', props.character.world], ['生日', props.character.birthday], ['守护神', props.character.deity],
] as const)
const equipmentSlots = ['main_hand', 'off_hand', 'head', 'body', 'hands', 'legs', 'feet']
const accessorySlots = ['ears', 'neck', 'wrists', 'right_ring', 'left_ring']

function gearFor(slots: string[]) {
  return slots.map(slot => props.character.gear.find(item => item.slot === slot)).filter(item => item !== undefined)
}

const equipment = computed(() => gearFor(equipmentSlots))
const accessories = computed(() => gearFor(accessorySlots))
const item = computed(() => selected.value?.candidates[candidate.value])
const itemStats = computed(() => {
  if (!item.value) return []
  return ([
    ['物理性能', item.value.physical_damage], ['魔法性能', item.value.magic_damage],
    ['物理防御力', item.value.physical_defense], ['魔法防御力', item.value.magic_defense],
  ] as const).filter(([, value]) => value && value > 0)
})

function cancelTimer() {
  if (closeTimer !== undefined) clearTimeout(closeTimer)
}

function close() {
  cancelTimer()
  selected.value = null
  pinned.value = false
}

function scheduleClose() {
  cancelTimer()
  if (!pinned.value) closeTimer = setTimeout(close, 140)
}

function reveal(entry: Equipment, target: HTMLElement, pin: boolean) {
  cancelTimer()
  if (pinned.value && !pin) return
  if (pinned.value && selected.value === entry) {
    close()
    return
  }
  const box = target.getBoundingClientRect()
  const width = Math.min(312, window.innerWidth - 28)
  position.value = {
    x: Math.min(window.innerWidth - width - 14, Math.max(14, box.left + box.width / 2 - width / 2)),
    y: Math.max(12, Math.min(box.bottom + 8, window.innerHeight - 328)),
  }
  if (selected.value !== entry) candidate.value = 0
  selected.value = entry
  pinned.value = pin
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') close()
}

onMounted(() => {
  window.addEventListener('keydown', onKey)
  window.addEventListener('resize', close)
  window.addEventListener('scroll', close, { passive: true })
})
onBeforeUnmount(() => {
  cancelTimer()
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('resize', close)
  window.removeEventListener('scroll', close)
})
</script>

<template>
  <div class="profile-sidebar">
    <section class="paper details-panel" aria-labelledby="details-title">
      <div class="panel-title">
        <span class="section-icon"><UiIcon name="book" :size="18" /></span>
        <h2 id="details-title">角色资料</h2>
      </div>
      <dl class="detail-grid">
        <div v-for="[label, value] in fields" :key="label">
          <dt>{{ label }}</dt>
          <dd :class="{ 'is-unknown': !value }">{{ value || '未获取' }}</dd>
        </div>
      </dl>
    </section>
    <JobLevels v-if="character.jobProgress" :progress="character.jobProgress" />
  </div>

  <div class="character-showcase">
    <PreviewStage :character="character">
      <template #equipment>
        <EquipmentRail label="装备" :items="equipment" :selected="selected" @reveal="reveal" @dismiss="scheduleClose" />
      </template>
      <template #accessories>
        <EquipmentRail label="饰品" :items="accessories" :selected="selected" @reveal="reveal" @dismiss="scheduleClose" />
      </template>
    </PreviewStage>
  </div>

  <Teleport to="body">
    <div v-if="selected && pinned" class="tooltip-cover" aria-hidden="true" @click="close"></div>
    <section v-if="selected" id="item-detail" class="item-tooltip"
      :style="{ '--tip-x': position.x + 'px', '--tip-y': position.y + 'px' }"
      aria-label="装备详情" @mouseenter="cancelTimer" @mouseleave="scheduleClose">
      <div class="tip-top">
        <span class="tip-slot">{{ selected.label }} · {{ selected.candidates.length > 1 ? '同模候选' : '展示外观' }}</span>
        <button type="button" class="icon-button" aria-label="关闭装备详情" @click="close"><UiIcon name="close" :size="17" /></button>
      </div>
      <div class="tip-title">
        <img v-if="icon(item)" :src="icon(item)" alt="" width="42" height="42">
        <h3>{{ item?.name || '模型 ' + selected.model }}</h3>
      </div>
      <label v-if="selected.candidates.length > 1" class="tip-candidates">
        <span>{{ selected.candidates.length }} 个同模候选</span>
        <select v-model="candidate" aria-label="查看同模候选">
          <option v-for="(option, index) in selected.candidates" :key="option.id" :value="index">{{ option.name }}</option>
        </select>
      </label>
      <dl v-if="item" class="tip-stats">
        <div v-if="item.item_level"><dt>物品品级</dt><dd>{{ item.item_level }}</dd></div>
        <div v-if="item.equip_level"><dt>装备等级</dt><dd>{{ item.equip_level }}</dd></div>
        <div v-for="[label, value] in itemStats" :key="label"><dt>{{ label }}</dt><dd>{{ value }}</dd></div>
      </dl>
      <div class="tip-model">
        <span>模型 {{ selected.model }} · 变体 {{ selected.variant }}</span>
        <span v-for="(dye, index) in selected.stains" :key="index" class="tip-dye">
          <span v-if="dye.color" :style="{ backgroundColor: dye.color }"></span>{{ dye.name }}
        </span>
      </div>
      <a v-if="item && item.id > 0" class="wiki-link" :href="wikiUrl(item)" target="_blank" rel="noopener noreferrer">在灰机 Wiki 查看</a>
    </section>
  </Teleport>
</template>
