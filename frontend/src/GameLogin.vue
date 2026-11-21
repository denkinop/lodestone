<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import {
  beginGameLogin, cancelGameOperation, getGameAreas, getGameProgress, getSavedGameArea, signOutGame,
  type GameArea, type GameProgress, type GameResponse,
} from './bridge'

const emit = defineEmits<{ back: []; authorized: [result: GameResponse] }>()
const mode = ref<'phone' | 'scan'>('phone')
const account = ref('')
const remember = ref(false)
const savedArea = ref<number | null>(null)
const areas = ref<GameArea[]>([])
const area = ref<number>()
const active = ref<string>()
const progress = ref<GameProgress>()
const error = ref('')
const cancelling = ref(false)
const loadingAreas = ref(false)
let poll: ReturnType<typeof setTimeout> | undefined
let mounted = true

async function loadAreas() {
  if (loadingAreas.value) return
  loadingAreas.value = true
  error.value = ''
  try {
    const result = await getGameAreas()
    if (result.error) error.value = result.error
    areas.value = result.areas
    if (!areas.value.some(option => option.id === area.value)) area.value = undefined
  } catch {
    error.value = '无法加载官方大区列表'
  } finally {
    loadingAreas.value = false
  }
}

function schedule(id: string) {
  poll = setTimeout(async () => {
    try {
      const value = await getGameProgress(id)
      if (active.value === id && value) progress.value = value
    } catch {
      // The login result reports success or failure.
    }
    if (active.value === id) schedule(id)
  }, 300)
}

async function login(loginMode: 'phone' | 'scan' | 'saved' = mode.value) {
  const selectedArea = area.value
  if (!mounted || active.value || selectedArea === undefined) return
  const id = crypto.randomUUID()
  const enteredAccount = loginMode === 'phone' ? account.value.trim() : ''
  active.value = id
  error.value = ''
  progress.value = { status: loginMode === 'saved' ? '正在恢复盛趣登录' : '正在连接盛趣', number: null, qr: null }
  schedule(id)
  try {
    const result = await beginGameLogin(id, loginMode, enteredAccount, selectedArea, loginMode !== 'saved' && remember.value)
    if (active.value !== id) return
    if (result.error) {
      error.value = result.error
      if (loginMode === 'saved') savedArea.value = await getSavedGameArea().catch(() => null)
    } else {
      clearOperation()
      emit('authorized', result)
    }
  } catch {
    if (active.value === id) error.value = '无法完成盛趣登录，请重试'
  } finally {
    if (active.value === id) clearOperation()
  }
}

function clearOperation() {
  clearTimeout(poll)
  active.value = undefined
  progress.value = undefined
  cancelling.value = false
}

async function cancel() {
  const id = active.value
  if (!id || cancelling.value) return
  cancelling.value = true
  try {
    await cancelGameOperation(id)
  } catch {
    if (active.value === id) {
      cancelling.value = false
      error.value = '无法取消当前请求'
    }
  }
}

async function forget() {
  try {
    if (await signOutGame()) savedArea.value = null
    else error.value = '无法清除已保存登录'
  } catch {
    error.value = '无法清除已保存登录'
  }
}

onMounted(async () => {
  const [, saved] = await Promise.all([loadAreas(), getSavedGameArea().catch(() => null)])
  if (!mounted) return
  savedArea.value = saved
  if (saved !== null && areas.value.some(option => option.id === saved)) {
    area.value = saved
    await login('saved')
  }
})
onBeforeUnmount(() => {
  mounted = false
  if (active.value) void cancelGameOperation(active.value).catch(() => {})
  clearOperation()
})
</script>

<template>
  <main class="narrow-page">
    <div class="page-heading">
      <div><h1>盛趣登录</h1></div>
      <button class="secondary" type="button" @click="emit('back')">返回</button>
    </div>
    <section class="paper settings-panel" aria-label="游戏登录">
      <form class="game-form" @submit.prevent="login()">
        <label for="game-area">大区</label>
        <select id="game-area" v-model="area" :disabled="Boolean(active) || loadingAreas" required>
          <option :value="undefined" disabled>{{ loadingAreas ? '正在读取大区…' : '请选择大区' }}</option>
          <option v-for="option in areas" :key="option.id" :value="option.id">{{ option.name }}</option>
        </select>
        <button v-if="!areas.length && !loadingAreas" class="secondary" type="button" @click="loadAreas">重新读取大区</button>
        <div v-if="savedArea !== null" class="game-actions">
          <button class="secondary" type="button" :disabled="Boolean(active) || area === undefined" @click="login('saved')">使用已保存登录</button>
          <button class="secondary" type="button" :disabled="Boolean(active)" @click="forget">清除已保存登录</button>
        </div>
        <label for="login-mode">登录方式</label>
        <select id="login-mode" v-model="mode" :disabled="Boolean(active)">
          <option value="phone">叨鱼确认</option>
          <option value="scan">二维码</option>
        </select>
        <label v-if="mode === 'phone'" for="sdo-account">盛趣账号</label>
        <input v-if="mode === 'phone'" id="sdo-account" v-model="account" :disabled="Boolean(active)" autocomplete="off" maxlength="128" required>
        <label class="game-remember"><input v-model="remember" type="checkbox" :disabled="Boolean(active)"> 在此设备保存登录状态</label>
        <button class="primary" type="submit" :disabled="Boolean(active) || area === undefined">
          {{ mode === 'phone' ? '发送确认' : '获取二维码' }}
        </button>
      </form>
    </section>
    <div v-if="progress" class="paper settings-panel game-progress" role="status">
      <p>{{ progress.status }}<strong v-if="progress.number"> · 确认编号 {{ progress.number }}</strong></p>
      <img v-if="progress.qr" :src="progress.qr" width="120" height="120" alt="叨鱼登录二维码">
      <button class="secondary" type="button" :disabled="cancelling" @click="cancel">取消</button>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
  </main>
</template>

<style scoped>
.game-form { display: grid; gap: 10px; justify-items: start; }
.game-form input:not([type=checkbox]), .game-form select { min-width: 12rem; padding: .45rem; border: 1px solid var(--line); border-radius: 6px; }
.game-remember { display: flex; align-items: center; gap: .4rem; }
.game-actions { display: flex; flex-wrap: wrap; gap: 10px; }
.game-progress { margin-top: 16px; }
.game-progress img { display: block; margin-block: 8px; image-rendering: pixelated; }
.game-progress button { margin-top: 8px; }
</style>
