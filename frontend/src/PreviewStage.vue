<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from 'vue'
import { preparePreview } from './bridge'
import { asset, portrait, type Character } from './demo'
import { jobIcon, jobName } from './jobs'
import { portraitPng } from './portrait-export'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ character: Character }>()
const view = ref<'full' | 'portrait'>('full')
const canvas = ref<HTMLCanvasElement>()
const live = ref(false)
const busy = ref(false)
const exporting = ref(false)
const message = ref('')
const aetherBackground = asset('export-background.png')
const currentJob = computed(() => {
  const progress = props.character.jobProgress
  if (!progress?.current) return null
  const id = progress.current
  return { id, level: progress.levels[id], demo: progress.source === 'demo' }
})
let viewer: import('./renderer/viewer.js').Viewer | undefined
let request: AbortController | undefined

function setView(mode: 'full' | 'portrait') {
  view.value = mode
  viewer?.setMode(mode)
}

async function imageFrom(url: string) {
  const image = new Image()
  image.src = url
  await image.decode()
  return image
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
  message.value = ''
  live.value = true
  busy.value = true
  const controller = new AbortController()
  request = controller
  try {
    await nextTick()
    const [runtime, resources] = await Promise.all([
      import('./renderer/viewer.js'),
      preparePreview(props.character.id),
    ])
    if (controller.signal.aborted) return
    const response = await fetch(resources.scene, { signal: controller.signal })
    if (!response.ok) {
      const failure = await response.json().catch(() => null)
      throw new Error(failure?.error || '无法加载预览模型')
    }
    const scene = await response.json()
    if (controller.signal.aborted || !canvas.value) return
    viewer = new runtime.Viewer(canvas.value)
    viewer.setMode(view.value)
    viewer.setHidden('head', true)
    const missing = await viewer.load(scene, controller.signal, (id: string) => resources.textureRoot + encodeURIComponent(id) + '.png')
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
    const [figure, backdrop] = await Promise.all([
      activeViewer?.canvas ?? imageFrom(portrait(props.character.id, mode)),
      imageFrom(aetherBackground),
    ])
    if (activeViewer !== viewer || mode !== view.value) return
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

onBeforeUnmount(stopLive)
</script>

<template>
  <section class="hero" aria-label="角色预览">
    <div class="hero-heading">
      <p><UiIcon name="location" :size="15" />{{ character.dc }} <span>/</span> {{ character.world }}</p>
      <h1>{{ character.name }}</h1>
      <div v-if="currentJob" class="hero-job">
        <img :src="jobIcon(currentJob.id)" alt="" width="23" height="23">
        <span>{{ jobName(currentJob.id) }}</span>
        <strong v-if="currentJob.level">Lv. {{ currentJob.level }}</strong>
        <small v-if="currentJob.demo">示例</small>
      </div>
    </div>
    <div class="hero-layout">
      <slot name="equipment" />
      <div class="hero-frame" :class="{ 'hero-still': !live || busy }">
        <img v-if="!live || busy" class="hero-aether" :src="aetherBackground" :class="{ 'aether-closeup': view === 'portrait' }" alt="">
        <div class="hero-art" :class="{ 'view-portrait': view === 'portrait', 'is-live': live && !busy }">
          <img v-show="!live || busy" class="hero-character" :src="portrait(character.id, view)" :alt="character.name + '的角色预览'">
          <div v-if="live" class="live-viewport">
            <canvas ref="canvas" v-show="!busy" tabindex="0" aria-label="三维角色预览，可拖动或用方向键旋转"></canvas>
          </div>
        </div>
        <button v-if="live && !busy" type="button" class="hero-reset" title="还原视角" aria-label="还原视角" @click="viewer?.reset()">
          <UiIcon name="focus" :size="18" />
        </button>
        <p v-if="message" class="hero-message" role="status">{{ message }}</p>
      </div>
      <slot name="accessories" />
    </div>
    <div class="hero-controls">
      <div class="hero-segment" aria-label="预览视图">
        <button type="button" :aria-pressed="view === 'full'" @click="setView('full')">全身</button>
        <button type="button" :aria-pressed="view === 'portrait'" @click="setView('portrait')">面部</button>
      </div>
      <div class="hero-actions">
        <button type="button" class="hero-button hero-live" :aria-pressed="live" @click="toggleLive">
          <UiIcon name="cube" :size="17" />
          <span>{{ busy ? '加载中' : live ? '退出 3D' : '3D 预览' }}</span>
        </button>
        <button type="button" class="hero-button hero-export" aria-label="导出 PNG" :disabled="busy || exporting" @click="save">
          <UiIcon name="download" :size="18" /><span>导出 PNG</span>
        </button>
      </div>
    </div>
  </section>
</template>
