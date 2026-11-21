<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  beginGameLogin, cancelGameOperation, getCommunityStatus, getGameAreas, getGameProgress, getSavedGameArea,
  openCommunity, readCommunityJobs, selectCommunityCharacter, selectServiceAccount, signOutGame,
  type CommunityStatus, type GameArea, type GameProgress, type GameResponse,
} from './bridge'
import JobLevels from './JobLevels.vue'
import { communityJobProgress, lobbyJobName } from './jobs'

const emit = defineEmits<{ back: [] }>()
const mode = ref<'phone' | 'scan'>('phone')
const account = ref('')
const remember = ref(false)
const savedArea = ref<number | null>(null)
const areas = ref<GameArea[]>([])
const area = ref<number>()
const active = ref<string>()
const progress = ref<GameProgress>()
const error = ref('')
const notice = ref('')
const cancelling = ref(false)
const loadingAreas = ref(false)
const roster = ref<GameResponse>()
const community = ref<CommunityStatus>()
const characterId = ref<string>()
const syncingCharacter = ref(false)
const syncError = ref('')
const character = computed(() => roster.value?.characters.find(item => item.id === characterId.value))
const communityProfileMatches = computed(() => {
  const profile = community.value?.jobProfile
  const selected = character.value
  return profile && selected && profile.character === selected.name && profile.area === selected.area
    && profile.world === selected.homeWorld
})
const jobs = computed(() => communityProfileMatches.value && community.value?.jobProfile
  ? communityJobProgress(community.value.jobProfile.levels, character.value?.classJobId ?? null) : undefined)
const jobsMessage = computed(() => {
  const state = community.value
  if (syncingCharacter.value) return '正在同步石之家角色'
  if (!state || state.state === 'starting' || state.jobsStatus === 'loading') return '正在读取石之家职业等级'
  if (state.state === 'unbound') return '点选角色后可同步石之家并读取职业等级。'
  if (state.state !== 'connected') return '验证石之家登录后可读取职业等级。'
  if (state.jobProfile && !communityProfileMatches.value)
    return `石之家返回的是 ${state.jobProfile.character} · ${state.jobProfile.world} 的等级。`
  return '石之家尚未返回所选角色的职业等级。'
})
const slotNames: Record<string, string> = {
  main_hand: '主手', off_hand: '副手', head: '头部', body: '身体', hands: '手部', legs: '腿部',
  feet: '脚部', ears: '耳饰', neck: '项链', wrists: '手镯', right_ring: '右戒指', left_ring: '左戒指',
}
let poll: ReturnType<typeof setTimeout> | undefined
let communityPoll: ReturnType<typeof setTimeout> | undefined
let communityGeneration = 0

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
      // The operation response reports success or failure.
    }
    if (active.value === id) schedule(id)
  }, 300)
}

async function run(operation: (id: string) => Promise<GameResponse>, status: string) {
  if (active.value) return
  error.value = ''
  const id = crypto.randomUUID()
  active.value = id
  progress.value = { status, number: null, qr: null }
  schedule(id)
  try {
    const result = await operation(id)
    if (active.value !== id) return
    if (result.error) error.value = result.error
    else {
      roster.value = result
      if (result.notice) notice.value = result.notice
      syncError.value = ''
      characterId.value = result.characters[0]?.id
      if (!communityPoll) void refreshCommunity(communityGeneration)
    }
  } catch {
    if (active.value === id) error.value = '无法完成请求，请稍后重试'
  } finally {
    if (active.value === id) clearOperation()
  }
}

async function refreshCommunity(generation: number) {
  try {
    const state = await getCommunityStatus()
    if (generation === communityGeneration && state) community.value = state
  } catch {
    // A community status request is independent of the game roster.
  }
  if (generation === communityGeneration && roster.value)
    communityPoll = setTimeout(() => refreshCommunity(generation), 1500)
}

async function showCommunity() {
  try {
    if (await openCommunity()) return
  } catch {
    // Keep the authenticated game session available.
  }
  community.value = { state: 'failed', detail: '无法打开石之家官方确认窗口', bindings: [], jobsStatus: 'idle', jobProfile: null }
}

async function reloadJobs() {
  try {
    if (!await readCommunityJobs()) syncError.value = '石之家会话暂时无法读取职业等级。'
  } catch {
    syncError.value = '无法重新读取石之家职业等级。'
  }
}

async function selectCharacter(id: string) {
  if (active.value || syncingCharacter.value) return
  characterId.value = id
  syncError.value = ''
  if (community.value?.state !== 'connected' && community.value?.state !== 'unbound') return
  const generation = communityGeneration
  syncingCharacter.value = true
  try {
    const message = await selectCommunityCharacter(id)
    if (generation === communityGeneration) syncError.value = message ?? ''
  } catch {
    if (generation === communityGeneration) syncError.value = '无法同步石之家角色。'
  } finally {
    if (generation === communityGeneration) syncingCharacter.value = false
  }
}

function clearCommunity() {
  communityGeneration++
  clearTimeout(communityPoll)
  communityPoll = undefined
  community.value = undefined
  syncingCharacter.value = false
  syncError.value = ''
}

function clearOperation() {
  clearTimeout(poll)
  active.value = undefined
  progress.value = undefined
  cancelling.value = false
}

function login() {
  const selectedArea = area.value
  const loginMode = mode.value
  const enteredAccount = loginMode === 'phone' ? account.value.trim() : ''
  if (selectedArea !== undefined) {
    notice.value = ''
    roster.value = undefined
    characterId.value = undefined
    void run(id => beginGameLogin(id, loginMode, enteredAccount, selectedArea, remember.value), '正在连接盛趣')
  }
}

async function restore() {
  const selectedArea = area.value
  if (selectedArea === undefined || active.value || savedArea.value === null) return
  notice.value = ''
  await run(id => beginGameLogin(id, 'saved', '', selectedArea, false), '正在恢复盛趣登录')
  if (!roster.value) await loadSavedArea()
}

async function loadSavedArea() {
  try {
    savedArea.value = await getSavedGameArea()
  } catch {
    savedArea.value = null
  }
}

function choose(index: number) {
  void run(id => selectServiceAccount(id, index), '正在读取角色列表')
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

async function logout() {
  clearOperation()
  clearCommunity()
  roster.value = undefined
  characterId.value = undefined
  account.value = ''
  remember.value = false
  savedArea.value = null
  notice.value = ''
  error.value = ''
  try {
    if (!await signOutGame()) error.value = '无法清除本机保存的登录状态'
  } catch {
    error.value = '无法确认游戏会话已清理'
  }
}

onMounted(async () => {
  await Promise.all([loadAreas(), loadSavedArea()])
  if (!active.value && area.value === undefined && savedArea.value !== null
    && areas.value.some(option => option.id === savedArea.value)) {
    area.value = savedArea.value
    await restore()
  }
})
onBeforeUnmount(() => {
  clearOperation()
  clearCommunity()
})
</script>

<template>
  <main class="narrow-page game-page">
    <div class="page-heading">
      <div><h1>{{ roster ? '国服角色档案' : '盛趣登录' }}</h1></div>
      <button class="secondary" type="button" @click="emit('back')">返回</button>
    </div>
    <section v-if="roster" class="paper settings-panel game-community" aria-labelledby="community-title">
      <h2 id="community-title">石之家</h2>
      <p role="status">{{ community?.detail ?? '正在准备社区会话' }}</p>
      <ul v-if="community?.bindings.length">
        <li v-for="(binding, index) in community.bindings" :key="index">{{ binding.character }} · {{ binding.world }}</li>
      </ul>
      <button type="button" class="secondary" :disabled="!community || community.state === 'starting'" @click="showCommunity">
        {{ community?.state === 'connected' || community?.state === 'unbound' ? '查看石之家' : '打开官方确认窗口' }}
      </button>
    </section>
    <section v-if="!roster" class="paper settings-panel" aria-label="游戏登录">
      <form class="game-form" @submit.prevent="login">
        <label for="game-area">大区</label>
        <select id="game-area" v-model="area" :disabled="Boolean(active) || loadingAreas" required>
          <option :value="undefined" disabled>{{ loadingAreas ? '正在读取大区…' : '请选择大区' }}</option>
          <option v-for="option in areas" :key="option.id" :value="option.id">{{ option.name }}</option>
        </select>
        <button v-if="!areas.length && !loadingAreas" class="secondary" type="button" @click="loadAreas">重新读取大区</button>
        <div v-if="savedArea !== null" class="game-actions">
          <button class="secondary" type="button" :disabled="Boolean(active) || area === undefined" @click="restore">使用已保存登录</button>
          <button class="secondary" type="button" :disabled="Boolean(active)" @click="logout">清除已保存登录</button>
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
        <p>客户端会尝试用本次盛趣登录授权石之家，额外确认时可在角色页继续。</p>
      </form>
    </section>
    <section v-else class="paper settings-panel" aria-label="游戏角色">
      <div class="game-actions">
        <button class="secondary" type="button" @click="logout">退出游戏登录</button>
        <label v-if="roster.accounts.length" for="service-account">服务账号</label>
        <select v-if="roster.accounts.length" id="service-account" :value="roster.selectedIndex ?? ''" :disabled="Boolean(active) || syncingCharacter"
          @change="choose(Number(($event.target as HTMLSelectElement).value))">
          <option value="" disabled>请选择服务账号</option>
          <option v-for="entry in roster.accounts" :key="entry.index" :value="entry.index">{{ entry.name || '服务账号 ' + (entry.index + 1) }}</option>
        </select>
      </div>
      <p v-if="!roster.accounts.length">此大区没有可用的服务账号。</p>
      <p v-else-if="roster.selectedIndex !== null && !roster.characters.length">所选服务账号没有角色。</p>
      <div v-if="roster.characters.length" class="game-characters">
        <button v-for="item in roster.characters" :key="item.id" class="secondary" type="button"
          :aria-pressed="item.id === characterId" :disabled="Boolean(active) || syncingCharacter"
          @click="selectCharacter(item.id)">{{ item.name }} · {{ item.homeWorld }}</button>
      </div>
      <p v-if="roster.characters.length && (community?.state === 'connected' || community?.state === 'unbound')" class="game-sync-note">
        点选角色会同步切换石之家的绑定角色。
      </p>
      <section v-if="character" class="game-character">
        <h2>{{ character.name }}</h2>
        <dl class="detail-grid">
          <div><dt>大区</dt><dd>{{ character.area }}</dd></div>
          <div><dt>所属服务器</dt><dd>{{ character.homeWorld }}</dd></div>
          <div><dt>当前服务器</dt><dd>{{ character.currentWorld }}</dd></div>
          <div><dt>种族</dt><dd>{{ character.race ?? '未获取' }}</dd></div>
          <div><dt>性别</dt><dd>{{ character.gender ?? '未获取' }}</dd></div>
          <div><dt>当前职业</dt><dd>{{ lobbyJobName(character.classJobId) ?? '未获取' }}</dd></div>
          <div><dt>当前职业等级</dt><dd>{{ character.level == null ? '未获取' : `Lv. ${character.level}` }}</dd></div>
        </dl>
        <JobLevels v-if="jobs" class="game-job-levels" :progress="jobs" />
        <div v-else class="game-job-levels">
          <p role="status">{{ jobsMessage }}</p>
          <button v-if="!syncingCharacter && (community?.state === 'unbound' || community?.state === 'connected' && community.jobProfile)"
            class="secondary" type="button" @click="selectCharacter(character.id)">同步所选角色</button>
          <button v-else-if="!syncingCharacter && community?.state === 'connected' && community.jobsStatus !== 'loading'"
            class="secondary" type="button" @click="reloadJobs">重新读取职业等级</button>
        </div>
        <p v-if="syncError" class="game-error" role="alert">{{ syncError }}</p>
        <p v-if="character.detailError" role="status">{{ character.detailError }}</p>
        <table v-if="character.gear.length" class="game-gear">
          <thead><tr><th scope="col">部位</th><th scope="col">模型</th><th scope="col">变体</th><th scope="col">染色</th></tr></thead>
          <tbody><tr v-for="item in character.gear" :key="item.slot">
            <th scope="row">{{ slotNames[item.slot] || item.slot }}</th>
            <td>{{ item.model }}<template v-if="item.modelType"> / {{ item.modelType }}</template></td>
            <td>{{ item.variant }}</td><td>{{ item.stain1 || '—' }} / {{ item.stain2 || '—' }}</td>
          </tr></tbody>
        </table>
      </section>
    </section>
    <div v-if="progress" class="paper settings-panel game-progress" role="status">
      <p>{{ progress.status }}<strong v-if="progress.number"> · 确认编号 {{ progress.number }}</strong></p>
      <img v-if="progress.qr" :src="progress.qr" width="120" height="120" alt="叨鱼登录二维码">
      <button class="secondary" type="button" :disabled="cancelling" @click="cancel">取消</button>
    </div>
    <p v-if="error" class="game-error" role="alert">{{ error }}</p>
    <p v-if="notice" class="game-error" role="status">{{ notice }}</p>
  </main>
</template>

<style scoped>
.game-form { display: grid; gap: 10px; justify-items: start; }
.game-form input:not([type=checkbox]), .game-page select { min-width: 12rem; padding: .45rem; border: 1px solid var(--line); border-radius: 6px; }
.game-remember { display: flex; align-items: center; gap: .4rem; }
.game-actions, .game-characters { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; }
.game-characters, .game-character, .game-progress, .game-error { margin-top: 16px; }
.game-job-levels { margin-block: 16px; }
.game-sync-note { margin-block: 8px 0; color: var(--muted); }
.game-progress img { display: block; margin-block: 8px; image-rendering: pixelated; }
.game-progress button { margin-top: 8px; }
.game-gear { width: 100%; margin-top: 16px; border-collapse: collapse; }
.game-gear th, .game-gear td { padding: .4rem; border-bottom: 1px solid var(--line); text-align: left; }
.game-error { color: #a12f28; }
.game-community { margin-bottom: 16px; }
.game-community p { margin-block: 8px; }
</style>
