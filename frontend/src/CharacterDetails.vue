<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue'
import { icon, wikiUrl, type Character, type Equipment } from './character'
import EquipmentRail from './EquipmentRail.vue'
import JobLevels from './JobLevels.vue'
import PreviewStage from './PreviewStage.vue'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ character: Character }>()
const selected = shallowRef<Equipment | null>(null)
const pinned = ref(false)
const position = ref<{ x: number; y: number }>()
const tooltip = ref<HTMLElement>()
const iconRoot = ref('')
let closeTimer: ReturnType<typeof setTimeout> | undefined

const fields = computed(() => [
  ['种族', props.character.race], ['部族', props.character.tribe], ['性别', props.character.gender],
  ['所属服务器', props.character.world], ['生日', props.character.birthday], ['守护神', props.character.deity],
] as const)
const equipmentSlots = ['head', 'body', 'hands', 'legs', 'feet']
const accessorySlots = ['off_hand', 'ears', 'neck', 'wrists', 'right_ring', 'left_ring']

function gearFor(slots: string[]) {
  return slots.map(slot => props.character.gear.find(item => item.slot === slot)).filter(item => item !== undefined)
}

const mainHand = computed(() => gearFor(['main_hand']))
const equipment = computed(() => gearFor(equipmentSlots))
const accessories = computed(() => gearFor(accessorySlots))
const item = computed(() => selected.value?.candidates[0])
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

async function reveal(entry: Equipment, target: HTMLElement, pin: boolean) {
  cancelTimer()
  if (pinned.value && !pin) return
  if (pinned.value && selected.value === entry) {
    close()
    return
  }
  position.value = undefined
  selected.value = entry
  pinned.value = pin
  await nextTick()
  if (!tooltip.value || selected.value !== entry) return
  const box = target.getBoundingClientRect()
  const { offsetWidth: width, offsetHeight: height } = tooltip.value
  const below = box.bottom + 8
  const maxY = window.innerHeight - height - 12
  position.value = {
    x: Math.min(window.innerWidth - width - 14, Math.max(14, box.left + box.width / 2 - width / 2)),
    y: Math.max(12, below <= maxY ? below : Math.min(box.top - height - 8, maxY)),
  }
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
    <JobLevels :progress="character.jobProgress" />
  </div>

  <div class="character-showcase">
    <PreviewStage :character="character" @resources="iconRoot = $event">
      <template #main-hand>
        <EquipmentRail label="主手" :items="mainHand" :selected="selected" :icon-root="iconRoot" @reveal="reveal" @dismiss="scheduleClose" />
      </template>
      <template #equipment>
        <EquipmentRail label="装备" :items="equipment" :selected="selected" :icon-root="iconRoot" @reveal="reveal" @dismiss="scheduleClose" />
      </template>
      <template #accessories>
        <EquipmentRail label="副手与饰品" :items="accessories" :selected="selected" :icon-root="iconRoot" @reveal="reveal" @dismiss="scheduleClose" />
      </template>
    </PreviewStage>
  </div>

  <Teleport to="body">
    <div v-if="selected && pinned" class="tooltip-cover" aria-hidden="true" @click="close"></div>
    <section v-if="selected" id="item-detail" ref="tooltip" class="item-tooltip"
      :style="position ? { '--tip-x': position.x + 'px', '--tip-y': position.y + 'px' } : { visibility: 'hidden' }"
      aria-label="装备详情" @mouseenter="cancelTimer" @mouseleave="scheduleClose">
      <div class="tip-title">
        <img v-if="icon(item, iconRoot)" :src="icon(item, iconRoot)" alt="" width="52" height="52">
        <div class="tip-name">
          <h3 :data-rarity="item?.rarity">{{ item?.name || '模型 ' + selected.model }}</h3>
          <p>{{ item?.category || selected.label }}</p>
        </div>
        <button type="button" class="icon-button" aria-label="关闭装备详情" @click="close"><UiIcon name="close" :size="17" /></button>
      </div>
      <dl v-if="itemStats.length" class="tip-performance">
        <div v-for="[label, value] in itemStats" :key="label"><dt>{{ label }}</dt><dd>{{ value }}</dd></div>
      </dl>
      <p v-if="item?.item_level" class="tip-grade">品级 <strong>{{ item.item_level }}</strong></p>
      <div v-if="item?.job_category || item?.equip_level" class="tip-requirements">
        <p v-if="item.job_category">{{ item.job_category }}</p>
        <p v-if="item.equip_level">{{ item.equip_level }} 级或更高</p>
      </div>
      <div v-if="item?.attributes?.length" class="tip-attributes">
        <p>装备属性</p>
        <dl>
          <div v-for="attribute in item.attributes" :key="attribute.name">
            <dt>{{ attribute.name }}</dt><dd>{{ attribute.value > 0 ? '+' : '' }}{{ attribute.value }}</dd>
          </div>
        </dl>
      </div>
      <div v-if="selected.stains.length" class="tip-dyes">
        <span v-for="(dye, index) in selected.stains" :key="index" class="tip-dye">
          <span v-if="dye.color" :style="{ backgroundColor: dye.color }"></span>{{ dye.name }}
        </span>
      </div>
      <div v-if="item && item.id > 0" class="tip-footer">
        <span v-if="selected.candidates.length > 1">外观参考</span>
        <a class="wiki-link" :href="wikiUrl(item)" target="_blank" rel="noopener noreferrer">在灰机 Wiki 查看</a>
      </div>
    </section>
  </Teleport>
</template>
