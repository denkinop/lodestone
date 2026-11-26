<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { getWardrobeDyes, prepareWardrobePreview, releaseWardrobePreview, searchWardrobe,
  type EquipmentStain, type GameCharacter, type GameGear, type WardrobeChange, type WardrobeItem } from './bridge'
import CameraHeightSlider from './CameraHeightSlider.vue'
import CharacterSettingsPrompt from './CharacterSettingsPrompt.vue'
import PageHeading from './PageHeading.vue'
import UiIcon from './UiIcon.vue'

const props = defineProps<{
  character?: GameCharacter | null
  prompt: string
  canOpenSettings: boolean
  draft: readonly WardrobeChange[]
}>()
const emit = defineEmits<{ settings: []; draft: [draft: readonly WardrobeChange[]] }>()
const groups = [
  { name: '装备', slots: [
    { key: 'head', label: '头部' }, { key: 'body', label: '身体' }, { key: 'hands', label: '手部' },
    { key: 'legs', label: '腿部' }, { key: 'feet', label: '脚部' },
  ] },
  { name: '饰品', slots: [
    { key: 'ears', label: '耳饰' }, { key: 'neck', label: '项链' }, { key: 'wrists', label: '手镯' },
    { key: 'left_ring', label: '左戒指' }, { key: 'right_ring', label: '右戒指' },
  ] },
]
const slotLabels = new Map(groups.flatMap(group => group.slots.map(slot => [slot.key, slot.label] as const)))

const canvas = ref<HTMLCanvasElement>()
const stage = ref<HTMLElement>()
const catalog = ref<HTMLElement>()
const catalogTitle = ref<HTMLElement>()
const dyeDialog = ref<HTMLDialogElement>()
const dyeChannel = ref(1)
const dyeQuery = ref('')
const dyes = shallowRef<EquipmentStain[]>()
const dyeBusy = ref(false)
const dyeError = ref(false)
const mobileListOpen = ref(false)
const selectedSlot = ref('body')
const query = ref('')
const items = shallowRef<WardrobeItem[]>([])
const more = ref(false)
const searchBusy = ref(false)
const searchError = ref('')
const previewBusy = ref(false)
const previewError = ref('')
const cameraHeight = ref(0)
const showHead = ref(true)
const showPortrait = ref(true)
const pending = shallowRef(props.draft)
const shown = shallowRef<{
  changes: readonly WardrobeChange[]
  covered: Record<string, string>
  applied: string[]
  gear: GameGear[]
}>()
const selectedLabel = computed(() => slotLabels.get(selectedSlot.value))
const selectedOwner = computed(() => shown.value?.covered[selectedSlot.value])
const selectedChange = computed(() => pending.value.find(change => change.slot === selectedSlot.value))
const selectedGear = computed(() => (shown.value?.gear ?? props.character?.gear)?.find(item => item.slot === selectedSlot.value))
const dyeChannels = computed(() => !selectedOwner.value && selectedGear.value?.loaded ? selectedGear.value.dyeChannels ?? 0 : 0)
const dyeOptions = computed(() => [
  { id: 0, name: '无染色', color: null }, ...(dyes.value ?? []),
].filter(dye => dye.name.includes(dyeQuery.value.trim())))
const selectedItem = computed(() => shown.value?.applied.includes(selectedSlot.value)
  ? shown.value.changes.find(change => change.slot === selectedSlot.value)?.itemId : undefined)
const canPreview = computed(() => Boolean(props.character?.race && props.character.gender))
const hasHead = computed(() => (shown.value?.gear ?? props.character?.gear)?.some(item => item.slot === 'head' && item.model > 0))
const iconRoot = '/_wardrobe/icons/'

let page = 0
let lastAttemptedPage = 0
let searchVersion = 0
let searchTimer: ReturnType<typeof setTimeout> | undefined
let previewTimer: ReturnType<typeof setTimeout> | undefined
let active: { id: string; controller: AbortController } | undefined
let viewer: import('./renderer/viewer.js').Viewer | undefined
let lastFailed: readonly WardrobeChange[] | undefined
let slotButton: HTMLElement | undefined

function summary(slot: string) {
  const state = shown.value
  const owner = state?.covered[slot]
  if (owner) return `${slotLabels.get(owner)}装备占用`
  const item = (state?.gear ?? props.character?.gear)?.find(entry => entry.slot === slot)
  if (!item?.model) return '空'
  const tried = state?.applied.includes(slot) && state.changes.find(change => change.slot === slot)?.name
  return tried || item.candidates[0]?.name || '已装备'
}

function openSlot(slot: string, event: MouseEvent) {
  if (selectedSlot.value !== slot) query.value = ''
  selectedSlot.value = slot
  slotButton = event.currentTarget as HTMLElement
  mobileListOpen.value = true
  void nextTick(() => {
    if (catalog.value && getComputedStyle(catalog.value).position === 'fixed') {
      stage.value?.scrollIntoView({ block: 'start' })
      catalogTitle.value?.focus({ preventScroll: true })
    }
  })
}

function closeList() {
  mobileListOpen.value = false
  slotButton?.focus({ preventScroll: true })
}

async function runSearch(targetPage: number, version = searchVersion) {
  searchBusy.value = true
  searchError.value = ''
  lastAttemptedPage = targetPage
  try {
    const response = await searchWardrobe(selectedSlot.value, query.value, targetPage)
    if (version !== searchVersion) return
    items.value = targetPage ? [...items.value, ...response.items] : response.items
    more.value = response.more
    page = targetPage
  } catch {
    if (version === searchVersion) searchError.value = '无法读取物品清单'
  } finally {
    if (version === searchVersion) searchBusy.value = false
  }
}

watch([selectedSlot, query], () => {
  if (!canPreview.value) return
  const version = ++searchVersion
  clearTimeout(searchTimer)
  items.value = []
  more.value = false
  searchError.value = ''
  searchBusy.value = true
  searchTimer = setTimeout(() => void runSearch(0, version), 140)
}, { immediate: true, flush: 'sync' })

function cancelPreview() {
  clearTimeout(previewTimer)
  if (!active) return
  active.controller.abort()
  void releaseWardrobePreview(active.id).catch(() => {})
  active = undefined
}

function schedulePreview(changes: readonly WardrobeChange[], delay = 120) {
  cancelPreview()
  pending.value = changes
  previewBusy.value = true
  previewError.value = ''
  lastFailed = undefined
  previewTimer = setTimeout(() => void updatePreview(changes), delay)
}

async function updatePreview(changes: readonly WardrobeChange[]) {
  if (!props.character || !canvas.value) return
  const operation = { id: crypto.randomUUID(), controller: new AbortController() }
  active = operation
  const { signal } = operation.controller
  try {
    const resources = await prepareWardrobePreview(props.character.id, operation.id, changes)
    if (signal.aborted) return
    const response = await fetch(resources.scene, { signal })
    if (!response.ok) throw new Error('无法读取试穿模型')
    const scene = await response.json()
    if (!viewer) {
      const { Viewer } = await import('./renderer/viewer.js')
      if (signal.aborted) return
      viewer = new Viewer(canvas.value)
      viewer.setHidden('head', !showHead.value)
    }
    const missing = await viewer.load(scene, signal, (id: string) => `${resources.textureRoot}${id}.png`, true)
    if (signal.aborted) return
    if (missing) throw new Error('有试穿贴图未能加载')
    shown.value = { changes, covered: resources.coveredSlots, applied: resources.appliedSlots, gear: scene.gear }
    emit('draft', changes)
  } catch (error) {
    if (signal.aborted) return
    lastFailed = changes
    pending.value = shown.value?.changes ?? props.draft
    previewError.value = error instanceof Error ? error.message : '试穿预览加载失败'
  } finally {
    if (active === operation) {
      active = undefined
      previewBusy.value = false
      void releaseWardrobePreview(operation.id).catch(() => {})
    }
  }
}

function changeSlot(itemId: number | undefined, name?: string) {
  const slot = selectedSlot.value
  const changes = pending.value.filter(change => change.slot !== slot)
  if (itemId !== undefined) {
    const current = selectedChange.value
    changes.push(current?.itemId === itemId ? current : { slot, itemId, name })
  }
  schedulePreview(changes)
}

async function loadDyes() {
  if (dyes.value || dyeBusy.value) return
  dyeError.value = false
  dyeBusy.value = true
  try {
    dyes.value = await getWardrobeDyes()
  } catch {
    dyeError.value = true
  } finally {
    dyeBusy.value = false
  }
}

function openDyes(channel: number) {
  dyeChannel.value = channel
  dyeQuery.value = ''
  dyeDialog.value?.showModal()
  void loadDyes()
}

function applyDye(id: number) {
  dyeDialog.value?.close()
  const index = dyeChannel.value - 1
  if ((selectedGear.value?.stains[index]?.id ?? 0) === id) return
  const slot = selectedSlot.value
  const current = selectedChange.value
  const change: WardrobeChange = { ...current, slot }
  const original = props.character?.gear.find(gear => gear.slot === slot)
  const base = change.itemId === undefined ? original?.stains[index]?.id ?? 0 : 0
  change[index === 0 ? 'stain1' : 'stain2'] = id === base ? undefined : id
  if (change.itemId === undefined && change.stain1 === undefined && change.stain2 === undefined) {
    schedulePreview(pending.value.filter(entry => entry.slot !== slot))
  } else {
    schedulePreview(current ? pending.value.map(entry => entry.slot === slot ? change : entry) : [...pending.value, change])
  }
}

function toggleHead() {
  showHead.value = !showHead.value
  viewer?.setHidden('head', !showHead.value)
}

function setCameraHeight(height: number) {
  cameraHeight.value = height
  viewer?.setCameraHeight(height)
}

function resetCamera() {
  cameraHeight.value = 0
  viewer?.reset()
}

function hideImage(event: Event) {
  (event.currentTarget as HTMLImageElement).hidden = true
}

onMounted(() => {
  if (canPreview.value) schedulePreview(props.draft, 0)
})
onBeforeUnmount(() => {
  clearTimeout(searchTimer)
  searchVersion++
  cancelPreview()
  viewer?.dispose()
})
</script>

<template>
  <main class="wardrobe-page">
    <PageHeading title="衣橱">
      <button v-if="canPreview" type="button" class="secondary" :disabled="!pending.length" @click="schedulePreview([])">全部还原</button>
    </PageHeading>

    <CharacterSettingsPrompt v-if="!canPreview || !character"
      :message="character ? '当前角色缺少外观数据，请在设置中刷新角色信息。' : prompt"
      :can-open-settings="canOpenSettings" @settings="emit('settings')" />

    <div v-else class="wardrobe-layout">
      <section ref="stage" class="paper wardrobe-stage" aria-label="试穿预览">
        <div class="wardrobe-figure">
          <div class="wardrobe-character">
            <h2>{{ character.name }}</h2>
            <p>{{ character.area }} · {{ character.homeWorld }}</p>
          </div>
          <img v-if="!shown && showPortrait" :src="`${character.portraitRoot}full.png`" class="wardrobe-portrait"
            alt="当前角色外观" @error="showPortrait = false">
          <canvas ref="canvas" tabindex="0" aria-label="3D 角色预览，可拖动或用方向键旋转" :style="{ visibility: shown ? undefined : 'hidden' }" />
          <div v-if="shown" class="wardrobe-camera" aria-label="预览视角控制">
            <CameraHeightSlider class="wardrobe-camera-height" :model-value="cameraHeight" @update:model-value="setCameraHeight" />
            <button type="button" aria-label="放大" title="放大" @click="viewer?.changeZoom(.85)">+</button>
            <button type="button" aria-label="缩小" title="缩小" @click="viewer?.changeZoom(1.18)">−</button>
            <button type="button" aria-label="还原视角" title="还原视角" @click="resetCamera"><UiIcon name="focus" :size="17" /></button>
          </div>
        </div>
        <div class="wardrobe-tools">
          <button type="button" class="wardrobe-toggle" :aria-pressed="showHead" :disabled="!hasHead" @click="toggleHead">头部装备</button>
          <p v-if="previewBusy" class="wardrobe-status" role="status">{{ shown ? '正在更新试穿…' : '正在加载角色…' }}</p>
        </div>
        <div v-if="previewError" class="wardrobe-preview-error">
          <p role="status">{{ previewError }}</p>
          <button type="button" class="secondary" @click="schedulePreview(lastFailed ?? pending, 0)">重试</button>
        </div>
      </section>

      <div class="wardrobe-selection">
        <section class="paper wardrobe-slots" aria-label="装备部位">
          <div v-for="group in groups" :key="group.name" class="wardrobe-slot-group">
            <h2>{{ group.name }}</h2>
            <div class="wardrobe-slot-grid">
              <button v-for="slot in group.slots" :key="slot.key" type="button" :aria-pressed="selectedSlot === slot.key"
                :aria-label="`${slot.label}，${summary(slot.key)}`" :title="summary(slot.key)" @click="openSlot(slot.key, $event)">
                <strong>{{ slot.label }}</strong>
                <small>{{ summary(slot.key) }}</small>
              </button>
            </div>
          </div>
        </section>

        <section ref="catalog" class="paper wardrobe-catalog" :class="{ 'is-open': mobileListOpen }"
          aria-labelledby="wardrobe-catalog-title" @keydown.esc="mobileListOpen && closeList()">
          <div class="wardrobe-catalog-header">
            <h2 id="wardrobe-catalog-title" ref="catalogTitle" tabindex="-1">{{ selectedLabel }}物品</h2>
            <button type="button" class="wardrobe-close secondary" aria-label="关闭物品清单" @click="closeList"><UiIcon name="close" /></button>
          </div>
          <div class="wardrobe-catalog-actions">
            <input v-model="query" type="search" maxlength="100" aria-label="搜索物品" placeholder="搜索名称或物品 ID">
            <button type="button" class="secondary" :disabled="!!selectedOwner || !selectedGear?.model || selectedChange?.itemId === 0"
              @click="changeSlot(0)">卸下</button>
            <button type="button" class="secondary" aria-label="还原原装备" :disabled="!selectedChange" @click="changeSlot(undefined)">还原</button>
          </div>
          <div v-if="selectedOwner || (selectedGear?.loaded && selectedGear.model)" class="wardrobe-current">
            <p v-if="selectedOwner" class="wardrobe-status wardrobe-coverage" role="status">
              {{ slotLabels.get(selectedOwner) }}装备占用此部位。选择新物品可替换该装备。
            </p>
            <div v-else-if="dyeChannels" class="wardrobe-dye-controls" role="group" aria-label="装备染色">
              <button v-for="channel in dyeChannels" :key="channel" type="button" class="secondary wardrobe-dye-button"
                aria-haspopup="dialog" :disabled="previewBusy" @click="openDyes(channel)">
                <span class="wardrobe-dye-swatch" :style="{ backgroundColor: selectedGear?.stains[channel - 1]?.color ?? undefined }"></span>
                <strong>{{ dyeChannels === 1 ? '染色' : `染色 ${channel}` }}</strong>
                <span>{{ selectedGear?.stains[channel - 1]?.name ?? '无染色' }}</span>
              </button>
            </div>
            <p v-else class="wardrobe-status">此装备不可染色</p>
          </div>
          <div class="wardrobe-results" :aria-busy="searchBusy">
            <ul v-if="items.length" class="wardrobe-items">
              <li v-for="item in items" :key="item.id">
                <button type="button" class="wardrobe-item" :aria-pressed="selectedItem === item.id" @click="changeSlot(item.id, item.name)">
                  <span class="wardrobe-item-icon" :data-rarity="item.rarity">
                    <UiIcon name="shirt" :size="17" />
                    <img v-if="item.icon" :src="`${iconRoot}${item.icon}.png`" alt="" loading="lazy" width="34" height="34" @error="hideImage">
                  </span>
                  <span class="wardrobe-item-text">
                    <strong>{{ item.name }}</strong>
                    <small>品级 {{ item.itemLevel }} · 装备等级 {{ item.equipLevel }}<template v-if="item.jobCategory"> · {{ item.jobCategory }}</template></small>
                    <small v-if="item.covers.length" class="wardrobe-item-coverage">占用 {{ item.covers.map(slot => slotLabels.get(slot)).join('、') }}</small>
                  </span>
                  <UiIcon v-if="selectedItem === item.id" name="check" :size="19" />
                </button>
              </li>
            </ul>
            <p v-if="searchBusy" class="wardrobe-list-status" role="status">正在读取物品…</p>
            <div v-else-if="searchError" class="wardrobe-list-status wardrobe-retry">
              <p role="status">{{ searchError }}</p>
              <button type="button" class="secondary" @click="runSearch(lastAttemptedPage)">重试</button>
            </div>
            <p v-else-if="!items.length" class="wardrobe-list-status" role="status">没有找到匹配的物品</p>
            <button v-else-if="more" type="button" class="secondary wardrobe-more" @click="runSearch(page + 1)">加载更多</button>
          </div>
        </section>
      </div>
    </div>
    <dialog ref="dyeDialog" class="wardrobe-dye-dialog" aria-labelledby="wardrobe-dye-title">
      <header>
        <h2 id="wardrobe-dye-title">{{ selectedLabel }} · {{ dyeChannels === 1 ? '选择染剂' : `染色 ${dyeChannel}` }}</h2>
        <button type="button" class="secondary" aria-label="关闭染剂" @click="dyeDialog?.close()"><UiIcon name="close" /></button>
      </header>
      <input v-model="dyeQuery" type="search" maxlength="50" aria-label="搜索染剂" placeholder="搜索染剂名称">
      <p v-if="dyeBusy" class="wardrobe-status" role="status">正在读取染剂…</p>
      <div v-else-if="dyeError" class="wardrobe-retry">
        <p class="wardrobe-status" role="status">无法读取染剂</p>
        <button type="button" class="secondary" @click="loadDyes">重试</button>
      </div>
      <ul v-else-if="dyeOptions.length" class="wardrobe-dye-options">
        <li v-for="dye in dyeOptions" :key="dye.id">
          <button type="button" :aria-pressed="(selectedGear?.stains[dyeChannel - 1]?.id ?? 0) === dye.id" @click="applyDye(dye.id)">
            <span class="wardrobe-dye-swatch" :style="{ backgroundColor: dye.color ?? undefined }"></span>
            {{ dye.name }}
          </button>
        </li>
      </ul>
      <p v-else class="wardrobe-status" role="status">没有找到匹配的染剂</p>
    </dialog>
  </main>
</template>

<style scoped>
.wardrobe-layout { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 18px; }
.wardrobe-stage { position: sticky; top: 14px; overflow: hidden; }
.wardrobe-figure {
  position: relative; height: clamp(390px, 65dvh, 660px);
  background: var(--preview-background);
}
.wardrobe-character { position: absolute; z-index: 1; top: 18px; left: 18px; pointer-events: none; }
.wardrobe-character h2 { color: var(--navy); font-size: 1.14rem; font-weight: 750; }
.wardrobe-character p { color: var(--muted); font-size: .8rem; margin-top: 4px; }
.wardrobe-figure canvas, .wardrobe-portrait { display: block; width: 100%; height: 100%; }
.wardrobe-figure canvas { touch-action: none; }
.wardrobe-figure canvas:focus-visible { outline-offset: -3px; }
.wardrobe-portrait { position: absolute; inset: 0; object-fit: contain; pointer-events: none; }
.wardrobe-camera {
  position: absolute; inset: 12px 12px 12px auto; display: flex; flex-direction: column;
  align-items: center; justify-content: end; gap: 5px; pointer-events: none;
}
.wardrobe-camera-height { flex: 1 1 148px; min-height: 0; max-height: 148px; margin-bottom: 4px; pointer-events: auto; }
.wardrobe-camera button {
  display: grid; place-items: center; flex: none; width: 42px; height: 42px; padding: 0; border: 1px solid var(--line);
  border-radius: var(--control-radius); background: #ffffffed; color: var(--navy); font-size: 1.12rem;
  pointer-events: auto;
}
.wardrobe-camera button:hover { background: var(--blue-light); }
.wardrobe-tools { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 7px 12px; padding: 9px 12px; }
.wardrobe-toggle {
  min-height: 36px; border: 1px solid var(--line); border-radius: 10px; padding: 5px 11px;
  background: var(--paper); color: var(--muted); font-size: .82rem; font-weight: 650;
}
.wardrobe-toggle[aria-pressed='true']:enabled { border-color: var(--blue-border); background: var(--blue-light); color: var(--blue-hover); }
.wardrobe-toggle:disabled { opacity: .55; }
.wardrobe-status { color: var(--muted); font-size: .82rem; }
.wardrobe-preview-error {
  display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 9px;
  padding: 9px 12px; border-top: 1px solid var(--line); font-size: .85rem;
}
/* The preview sets the height of both columns. The catalog scrolls within the remaining space. */
.wardrobe-selection { display: grid; grid-template-rows: auto minmax(0, 1fr); gap: 14px; min-width: 0; contain: size; }
.wardrobe-slots, .wardrobe-catalog { padding: 15px; }
.wardrobe-slot-group + .wardrobe-slot-group { margin-top: 13px; }
.wardrobe-slot-group h2, .wardrobe-catalog h2 { color: var(--navy); font-size: .98rem; }
.wardrobe-slot-grid { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 5px; margin-top: 7px; }
.wardrobe-slot-grid button {
  min-width: 0; min-height: 48px; border: 1px solid var(--line); border-radius: 10px;
  padding: 5px; background: var(--surface); text-align: center;
}
.wardrobe-slot-grid button[aria-pressed='true'] { border-color: var(--blue-border); background: var(--blue-light); color: var(--blue-hover); }
.wardrobe-slot-grid strong { display: block; font-size: .82rem; }
.wardrobe-slot-grid small { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); font-size: .73rem; }
.wardrobe-catalog { display: grid; grid-template-rows: auto auto minmax(0, 1fr); gap: 10px; min-height: 0; }
.wardrobe-catalog:has(.wardrobe-current) { grid-template-rows: auto auto auto minmax(0, 1fr); }
.wardrobe-catalog-header, .wardrobe-catalog-actions { display: flex; align-items: center; gap: 6px; }
.wardrobe-catalog-header { justify-content: space-between; }
.wardrobe-catalog-actions input {
  flex: 1; min-width: 0; min-height: 40px; padding: 6px 10px; border: 1px solid var(--line);
  border-radius: var(--control-radius); background: var(--surface); font-size: .88rem;
}
.wardrobe-catalog-actions button { flex: none; padding-inline: 10px; }
.wardrobe-close { display: none; }
.wardrobe-coverage { color: var(--amber); }
.wardrobe-dye-controls { display: flex; flex-wrap: wrap; gap: 6px; }
.wardrobe-dye-button { display: flex; min-width: 0; align-items: center; gap: 7px; min-height: 38px; }
.wardrobe-dye-button > span:last-child { color: var(--muted); font-weight: 400; }
.wardrobe-dye-swatch {
  display: block; width: 18px; height: 18px; flex: none; border-radius: 5px;
  background-color: var(--paper); border: 1px solid #303a4c40;
}
.wardrobe-dye-dialog {
  width: min(490px, calc(100% - 26px)); max-height: min(680px, 82dvh); overflow-y: auto; overscroll-behavior: contain;
  padding: 16px; color: var(--ink); background: var(--paper); border: 1px solid var(--line); border-radius: var(--panel-radius);
}
.wardrobe-dye-dialog::backdrop { background: var(--scrim); }
.wardrobe-dye-dialog header { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.wardrobe-dye-dialog h2 { color: var(--navy); font-size: 1.05rem; }
.wardrobe-dye-dialog header button { display: grid; place-items: center; min-width: 40px; padding: 0; }
.wardrobe-dye-dialog input {
  width: 100%; min-height: 40px; margin: 8px 0 10px; padding: 6px 10px;
  border: 1px solid var(--line); border-radius: var(--control-radius); background: var(--surface); font-size: .88rem;
}
.wardrobe-dye-options { display: grid; grid-template-columns: repeat(auto-fit, minmax(118px, 1fr)); gap: 5px; margin: 0; padding: 0; list-style: none; }
.wardrobe-dye-options button {
  display: flex; width: 100%; min-height: 42px; align-items: center; gap: 7px; text-align: left; padding: 6px 8px;
  border: 1px solid var(--line); border-radius: var(--control-radius); background: var(--surface); font-size: .82rem;
}
.wardrobe-dye-options button:hover, .wardrobe-dye-options button[aria-pressed='true'] {
  border-color: var(--blue-border); background: var(--blue-light); color: var(--blue-hover);
}
.wardrobe-results { min-height: 0; overflow: auto; overscroll-behavior: contain; }
.wardrobe-items { display: grid; gap: 4px; padding: 0; margin: 0; list-style: none; }
.wardrobe-item {
  display: flex; align-items: center; gap: 9px; width: 100%; min-height: 52px; padding: 6px 8px;
  text-align: left; border: 1px solid transparent; border-radius: var(--control-radius); background: transparent;
}
.wardrobe-item:hover { background: var(--surface); }
.wardrobe-item[aria-pressed='true'] { border-color: var(--blue-border); background: var(--blue-light); color: var(--blue-hover); }
.wardrobe-item:focus-visible { outline-offset: -2px; }
.wardrobe-item-icon {
  position: relative; display: grid; flex: none; place-items: center; width: 36px; height: 36px;
  overflow: hidden; border: 1px solid var(--line); border-radius: 8px; color: var(--muted); background: var(--surface);
}
.wardrobe-item-icon[data-rarity='2'] { border-color: var(--teal); }
.wardrobe-item-icon[data-rarity='3'] { border-color: var(--blue-border); }
.wardrobe-item-icon[data-rarity='4'] { border-color: var(--violet); }
.wardrobe-item-icon img { position: absolute; inset: 0; }
.wardrobe-item-icon img[hidden] { display: none; }
.wardrobe-item-text { min-width: 0; flex: 1; overflow-wrap: anywhere; }
.wardrobe-item-text strong, .wardrobe-item-text small { display: block; }
.wardrobe-item-text strong { font-size: .89rem; }
.wardrobe-item-text small { color: var(--muted); font-size: .75rem; margin-top: 2px; }
.wardrobe-item-text .wardrobe-item-coverage { color: var(--amber); }
.wardrobe-list-status { padding: 12px 8px; color: var(--muted); font-size: .88rem; }
.wardrobe-retry { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.wardrobe-more { width: 100%; margin-top: 8px; }

@container app-content (max-width: 720px) {
  .wardrobe-layout { grid-template-columns: minmax(0, 1fr); gap: 12px; }
  .wardrobe-layout:has(.wardrobe-catalog.is-open) { padding-bottom: 43dvh; }
  .wardrobe-selection { grid-template-rows: auto; contain: none; }
  .wardrobe-stage { position: static; scroll-margin-top: calc(58px + env(safe-area-inset-top)); }
  .wardrobe-figure { height: clamp(228px, 41dvh, 420px); }
  .wardrobe-character { top: 12px; left: 12px; }
  .wardrobe-camera { inset: 8px 8px 8px auto; }
  .wardrobe-camera button { width: 44px; height: 44px; }
  .wardrobe-slots { padding: 12px; }
  .wardrobe-catalog { display: none; }
  .wardrobe-catalog.is-open {
    position: fixed; z-index: 6; inset: auto 0 0; display: grid; width: min(100%, 640px); max-height: 43dvh;
    margin-inline: auto; padding: 10px 12px calc(10px + env(safe-area-inset-bottom));
    border-radius: var(--panel-radius) var(--panel-radius) 0 0; box-shadow: 0 -8px 35px #242d392b;
  }
  .wardrobe-close { display: grid; place-items: center; min-width: 44px; padding: 0; }
}
</style>
