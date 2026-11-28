<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import {
  beginGameLogin, cancelGameOperation, getGameAreas, getGameProgress, getSavedGameArea, signOutGame,
  type GameArea, type GameProgress, type GameResponse,
} from './bridge'

const props = defineProps<{ logoutError: string; renewing?: boolean }>()
const emit = defineEmits<{ authorized: [result: GameResponse] }>()
const mode = ref<'phone' | 'scan'>('phone')
const account = ref('')
const remember = ref(false)
const savedArea = ref<number | null>(null)
const areas = ref<GameArea[]>([])
const area = ref<number>()
const active = ref<string>()
const progress = ref<GameProgress>()
const error = ref(props.logoutError)
const areaNotice = ref('')
const cancelling = ref(false)
const loadingAreas = ref(false)
const checkingSaved = ref(true)
let poll: ReturnType<typeof setTimeout> | undefined
let mounted = true

async function loadAreas(refresh = false) {
  if (loadingAreas.value) return
  loadingAreas.value = true
  areaNotice.value = ''
  if (refresh) error.value = ''
  try {
    const result = await getGameAreas(refresh)
    if (result.error && !error.value) error.value = result.error
    areaNotice.value = result.notice ?? ''
    if (result.areas.length) {
      areas.value = result.areas
      if (!areas.value.some(option => option.id === area.value)) area.value = undefined
    }
  } catch {
    if (!error.value) error.value = '无法加载官方大区列表'
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
  const selectedArea = loginMode === 'saved' ? savedArea.value : area.value
  if (!mounted || active.value || selectedArea == null) return false
  const id = crypto.randomUUID()
  const enteredAccount = loginMode === 'phone' ? account.value.trim() : ''
  active.value = id
  error.value = ''
  progress.value = {
    status: loginMode === 'saved' ? '正在读取已保存的角色信息' : '正在连接盛趣',
    number: null, qr: null, canCancel: true,
  }
  schedule(id)
  try {
    const result = await beginGameLogin(id, loginMode, enteredAccount, selectedArea, loginMode !== 'saved' && remember.value)
    if (active.value !== id) return false
    if (result.error) {
      error.value = result.error
      if (loginMode === 'saved') savedArea.value = await getSavedGameArea().catch(() => null)
    } else {
      clearOperation()
      emit('authorized', result)
      return true
    }
  } catch {
    if (active.value === id) error.value = loginMode === 'saved' ? '无法打开已保存的角色信息' : '无法完成盛趣登录，请重试'
  } finally {
    if (active.value === id) clearOperation()
  }
  return false
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
    const accepted = await cancelGameOperation(id)
    if (!accepted && active.value === id) {
      cancelling.value = false
      if (progress.value) progress.value = { ...progress.value, canCancel: false }
    }
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
  const saved = await getSavedGameArea().catch(() => null)
  if (!mounted) return
  savedArea.value = saved
  if (saved !== null) {
    area.value = saved
    if (props.renewing) remember.value = true
  }
  checkingSaved.value = false
  await loadAreas()
})
onBeforeUnmount(() => {
  mounted = false
  if (active.value) void cancelGameOperation(active.value).catch(() => {})
  clearOperation()
})
</script>

<template>
  <div aria-label="盛趣登录">
    <form class="game-form" @submit.prevent="login()">
      <div class="game-field">
        <label for="game-area">大区</label>
        <select id="game-area" v-model="area" :disabled="Boolean(active) || checkingSaved || loadingAreas" required>
          <option :value="undefined" disabled>{{ loadingAreas ? '正在读取大区…' : '请选择大区' }}</option>
          <option v-for="option in areas" :key="option.id" :value="option.id">{{ option.name }}</option>
        </select>
      </div>
      <div class="game-actions">
        <button class="secondary" type="button" :disabled="Boolean(active) || checkingSaved || loadingAreas"
          @click="loadAreas(true)">刷新大区列表</button>
      </div>
      <p v-if="areaNotice" role="status">{{ areaNotice }}</p>
      <div v-if="savedArea !== null && !renewing" class="game-actions">
        <button class="secondary" type="button" :disabled="Boolean(active)" @click="login('saved')">使用已保存登录</button>
        <button class="secondary" type="button" :disabled="Boolean(active)" @click="forget">清除已保存登录</button>
      </div>
      <div class="game-field">
        <label for="login-mode">登录方式</label>
        <select id="login-mode" v-model="mode" :disabled="Boolean(active)">
          <option value="phone">叨鱼确认</option>
          <option value="scan">二维码</option>
        </select>
      </div>
      <div v-if="mode === 'phone'" class="game-field">
        <label for="sdo-account">盛趣账号</label>
        <input id="sdo-account" v-model="account" :disabled="Boolean(active)" autocomplete="off" maxlength="128" required>
      </div>
      <label class="game-remember"><input v-model="remember" type="checkbox" :disabled="Boolean(active)"> 在此设备保存登录状态</label>
      <div class="game-actions">
        <button class="primary" type="submit" :disabled="Boolean(active) || checkingSaved || loadingAreas || area === undefined">
          {{ mode === 'phone' ? '发送确认' : '获取二维码' }}
        </button>
        <slot name="actions" v-if="!active" />
      </div>
    </form>
    <div v-if="progress" class="paper settings-panel game-progress" role="status">
      <p>{{ progress.status }}<strong v-if="progress.number"> · 确认编号 {{ progress.number }}</strong></p>
      <img v-if="progress.qr" :src="progress.qr" width="120" height="120" alt="叨鱼登录二维码">
      <button v-if="progress.canCancel" class="secondary" type="button" :disabled="cancelling" @click="cancel">取消</button>
    </div>
    <p v-if="error" class="login-error" role="alert">{{ error }}</p>
  </div>
</template>

<style scoped>
.game-form { display: grid; gap: 10px; }
.game-field { display: grid; grid-template-columns: 5.25rem minmax(0, 1fr); align-items: center; gap: 8px; font-size: .92rem; }
.game-field input, .game-field select {
  width: min(100%, 21rem);
  min-height: 40px;
  padding: 6px 10px;
  border: 1px solid var(--line);
  border-radius: var(--control-radius);
  background: var(--surface);
}
.game-remember { display: flex; align-items: center; gap: .4rem; min-height: 34px; color: var(--muted); font-size: .875rem; }
.game-remember input { accent-color: var(--blue); }
.game-actions { display: flex; flex-wrap: wrap; gap: 10px; }
.game-progress { margin-top: 16px; }
.game-progress img { display: block; margin-block: 8px; image-rendering: pixelated; }
.game-progress button { margin-top: 8px; }
.login-error { margin-top: 12px; }
</style>
