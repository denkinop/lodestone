<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { prepareGamePreview, releaseGamePreview, saveGamePortrait, type GameGear } from './bridge'
import type { Character } from './character'
import { jobIcon, jobName, lobbyJobIcon, lobbyJobName } from './jobs'
import { portraitPng } from './portrait-export'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ character: Character }>()
const emit = defineEmits<{ resources: [iconRoot: string] }>()
type View = 'full' | 'portrait'
type Viewer = import('./renderer/viewer.js').Viewer
type Resources = { scene: string; textureRoot: string }
const view = ref<View>('full')
const canvas = ref<HTMLCanvasElement>()
const offscreen = ref<HTMLCanvasElement>()
const live = ref(false)
const busy = ref(false)
const capturing = ref(false)
const exporting = ref(false)
const message = ref('')
const loadedSlots = ref<string[]>([])
const hideHelmet = ref(true)
const aetherBackground = import.meta.env.BASE_URL + 'images/portrait-background.jpg'
const portraits = ref<Partial<Record<View, string>>>({})
const staticPortrait = computed(() => portraits.value[view.value])
const currentJob = computed(() => {
  const { jobProgress, jobId, level } = props.character
  if (jobProgress?.current) {
    const id = jobProgress.current
    return { name: jobName(id), level: jobProgress.levels[id] ?? level, icon: jobIcon(id) }
  }
  return { name: lobbyJobName(jobId), level, icon: lobbyJobIcon(jobId) }
})
let viewer: Viewer | undefined
let request: AbortController | undefined
let nativeRequest: string | undefined
let prepared: ReturnType<typeof prepareGamePreview> | undefined
const lifetime = new AbortController()

function setView(mode: View) {
  view.value = mode
  viewer?.setMode(mode)
  viewer?.setHidden('head', hideHelmet.value || mode === 'portrait')
}

async function imageFrom(url: string) {
  const image = new Image()
  image.src = url
  await image.decode()
  return image
}

async function loadScene(resources: Resources, signal: AbortSignal) {
  const response = await fetch(resources.scene, { signal })
  if (!response.ok) throw new Error('无法加载预览模型')
  return await response.json()
}

function loadViewer(viewer: Viewer, scene: Awaited<ReturnType<typeof loadScene>>, resources: Resources, signal: AbortSignal) {
  return viewer.load(scene, signal, (id: string) => resources.textureRoot + encodeURIComponent(id) + '.png')
}

function prepareGame() {
  if (prepared) return prepared
  const id = crypto.randomUUID()
  nativeRequest = id
  prepared = prepareGamePreview(props.character.id, id).catch(error => {
    if (nativeRequest === id) {
      nativeRequest = undefined
      prepared = undefined
    }
    throw error
  })
  return prepared
}

async function renderPortraits() {
  const { appearance, portraitRoot } = props.character
  if (!appearance) return
  const signal = lifetime.signal
  capturing.value = true
  message.value = ''
  let captureViewer: Viewer | undefined
  try {
    const resources = await prepareGame()
    if (signal.aborted) return
    for (const mode of ['full', 'portrait'] as const)
      if (resources[mode]) portraits.value[mode] = portraitRoot + mode + '.png'
    const scene = await loadScene(resources, signal)
    if (signal.aborted) return
    loadedSlots.value = scene.gear.filter((item: GameGear) => item.loaded).map((item: GameGear) => item.slot)
    emit('resources', resources.iconRoot)
    if (resources.full && resources.portrait) return
    const runtime = await import('./renderer/viewer.js')
    if (signal.aborted || !offscreen.value || !nativeRequest) return
    captureViewer = new runtime.Viewer(offscreen.value)
    captureViewer.setHidden('head', true)
    await loadViewer(captureViewer, scene, resources, signal)
    for (const mode of ['full', 'portrait'] as const) {
      if (signal.aborted) return
      if (resources[mode]) continue
      captureViewer.setMode(mode)
      captureViewer.render()
      const png = captureViewer.canvas.toDataURL('image/png')
      const saved = await saveGamePortrait(nativeRequest, mode, png).catch(() => false)
      if (!signal.aborted) portraits.value[mode] = saved ? portraitRoot + mode + '.png' : png
    }
  } catch (error) {
    if (!signal.aborted) message.value = error instanceof Error ? error.message : '无法生成角色图片'
  } finally {
    captureViewer?.dispose()
    capturing.value = false
  }
}

function stopLive() {
  request?.abort()
  request = undefined
  viewer?.dispose()
  viewer = undefined
  live.value = false
  busy.value = false
}

async function toggleLive() {
  if (live.value) {
    stopLive()
    return
  }
  if (capturing.value) return
  if (!portraits.value.full) await renderPortraits()
  if (lifetime.signal.aborted) return
  message.value = ''
  live.value = true
  busy.value = true
  const controller = new AbortController()
  request = controller
  try {
    await nextTick()
    if (controller.signal.aborted) return
    const [runtime, resources] = await Promise.all([
      import('./renderer/viewer.js'),
      prepareGame(),
    ])
    const scene = await loadScene(resources, controller.signal)
    if (controller.signal.aborted || !canvas.value) return
    viewer = new runtime.Viewer(canvas.value)
    viewer.setMode(view.value)
    viewer.setHidden('head', hideHelmet.value || view.value === 'portrait')
    const missing = await loadViewer(viewer, scene, resources, controller.signal)
    if (!controller.signal.aborted && missing) message.value = '有 ' + missing + ' 张贴图未能加载'
  } catch (error) {
    if (controller.signal.aborted) return
    stopLive()
    message.value = error instanceof Error ? error.message : '3D 预览加载失败'
  } finally {
    if (request === controller) {
      busy.value = false
      await nextTick()
      if (request === controller) viewer?.render()
    }
  }
}

async function save() {
  exporting.value = true
  message.value = ''
  try {
    const activeViewer = viewer
    const mode = view.value
    const figureSource = activeViewer?.canvas ?? (staticPortrait.value ? imageFrom(staticPortrait.value) : undefined)
    if (!figureSource) return
    const [figure, backdrop] = await Promise.all([figureSource, imageFrom(aetherBackground)])
    if (activeViewer !== viewer || mode !== view.value || lifetime.signal.aborted) return
    activeViewer?.render()
    const link = document.createElement('a')
    link.download = [props.character.name, props.character.world, mode === 'full' ? '全身' : '面部'].join('-') + '.png'
    link.href = portraitPng(figure, backdrop, mode)
    link.click()
  } catch (error) {
    message.value = error instanceof Error ? error.message : 'PNG 导出失败'
  } finally {
    exporting.value = false
  }
}

onMounted(renderPortraits)
onBeforeUnmount(() => {
  lifetime.abort()
  stopLive()
  if (nativeRequest) void releaseGamePreview(nativeRequest).catch(() => {})
})
</script>

<template>
  <section class="hero" aria-label="角色预览">
    <div class="hero-heading">
      <p><UiIcon name="location" :size="15" />{{ character.dc }} <span>/</span> {{ character.world }}</p>
      <h1>{{ character.name }}</h1>
    </div>
    <div class="hero-loadout">
      <slot name="main-hand" />
      <div v-if="currentJob.name" class="hero-job">
        <img v-if="currentJob.icon" :src="currentJob.icon" alt="" width="23" height="23">
        <span>{{ currentJob.name }}</span>
        <strong v-if="currentJob.level">Lv. {{ currentJob.level }}</strong>
      </div>
    </div>
    <div class="hero-layout">
      <slot name="equipment" />
      <div class="hero-frame" :class="{ 'hero-still': !live || busy }">
        <img v-if="!live || busy" class="hero-aether" :src="aetherBackground" :class="{ 'aether-closeup': view === 'portrait' }" alt="">
        <div class="hero-art" :class="{ 'view-portrait': view === 'portrait', 'is-live': live && !busy }">
          <img v-if="staticPortrait && (!live || busy)" class="hero-character" :src="staticPortrait" :alt="character.name + '的角色预览'">
          <div v-if="live" class="live-viewport">
            <canvas ref="canvas" v-show="!busy" tabindex="0" aria-label="三维角色预览，可拖动或用方向键旋转"></canvas>
          </div>
        </div>
        <button v-if="live && !busy" type="button" class="hero-reset" title="还原视角" aria-label="还原视角" @click="viewer?.reset()">
          <UiIcon name="focus" :size="18" />
        </button>
        <p v-if="message || (capturing && !staticPortrait) || !character.appearance" class="hero-message" role="status">
          {{ message || (capturing ? '正在准备角色图片…' : character.detailError || '暂无角色外观') }}
        </p>
      </div>
      <slot name="accessories" />
    </div>
    <div class="hero-controls">
      <div class="hero-segment" aria-label="预览视图">
        <button type="button" :aria-pressed="view === 'full'" @click="setView('full')">全身</button>
        <button type="button" :aria-pressed="view === 'portrait'" @click="setView('portrait')">面部</button>
      </div>
      <div class="hero-actions">
        <button type="button" class="hero-button hero-live" :aria-pressed="live"
          :disabled="capturing || !character.appearance" @click="toggleLive">
          <UiIcon name="cube" :size="17" />
          <span>{{ busy ? '取消加载' : live ? '退出 3D' : '3D 预览' }}</span>
        </button>
        <button type="button" class="hero-button hero-export" aria-label="导出 PNG"
          :disabled="busy || exporting || (!live && !staticPortrait)" @click="save">
          <UiIcon name="download" :size="18" /><span>导出 PNG</span>
        </button>
      </div>
    </div>
    <div v-if="live && !busy && loadedSlots.includes('head')">
      <button class="hero-button" type="button"
        :disabled="view === 'portrait'" :aria-pressed="!hideHelmet && view !== 'portrait'"
        @click="hideHelmet = !hideHelmet; viewer?.setHidden('head', hideHelmet)">显示头部装备</button>
    </div>
    <div v-if="capturing" class="portrait-capture" aria-hidden="true"><canvas ref="offscreen"></canvas></div>
  </section>
</template>

<style scoped>
.portrait-capture { position: fixed; left: -10000px; top: 0; width: 440px; height: 600px; visibility: hidden; }
.portrait-capture canvas { display: block; width: 100%; height: 100%; }
</style>
