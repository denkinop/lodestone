<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import CharacterDetails from './CharacterDetails.vue'
import GameVersionSettings from './GameVersionSettings.vue'
import GameLogin from './GameLogin.vue'
import UiIcon from './UiIcon.vue'
import { asset, portrait, type Character } from './demo'
import { communityJobProgress } from './jobs'
import {
  chooseGameCharacter, getCommunityStatus, getSavedGameArea, refreshLobby, selectCommunityCharacter,
  selectServiceAccount, signOutGame, type CommunityStatus, type GameCharacter, type GameResponse,
} from './bridge'

type Screen = 'login' | 'choose' | 'profile' | 'settings' | 'game'
const activeKey = 'lodestone-demo-character'
const activeId = ref<number | null>(Number(localStorage.getItem(activeKey)) || null)
const previewPage = new URLSearchParams(location.search).get('preview') === 'profile'
const screen = ref<Screen>(activeId.value !== null || previewPage ? 'profile' : 'login')
const demos = ref<Character[]>([])
const game = ref<GameResponse>()
const community = ref<CommunityStatus>()
const loadError = ref('')
const status = ref('')
const choosing = ref(false)
const lobbyBusy = ref(false)
let poll: ReturnType<typeof setTimeout> | undefined
let generation = 0
let attemptedRole: string | undefined
let syncingCommunity = false

const cover = computed(() => demos.value[0])
const gameCharacter = computed(() => game.value?.current)
const cachedProfile = computed(() => gameCharacter.value ? toProfile(gameCharacter.value) : undefined)
const profile = computed(() => {
  const cached = cachedProfile.value
  if (!cached) return demos.value.find(character => character.id === activeId.value)
  const jobs = community.value?.jobProfile
  const matches = jobs?.character === cached.name && jobs.area === cached.dc && jobs.world === cached.world
  return matches ? { ...cached, jobProgress: communityJobProgress(jobs.levels, gameCharacter.value?.classJobId ?? null) } : cached
})
const list = computed(() => game.value ? game.value.characters.map(toProfile) : demos.value)
const profileKey = computed(() => gameCharacter.value ? gameCharacter.value.id + ':' + gameCharacter.value.revision : profile.value?.id)

function toProfile(character: GameCharacter): Character {
  return {
    id: character.id, name: character.name, world: character.homeWorld, dc: character.area,
    race: character.race ?? '', tribe: character.tribe ?? '', gender: character.gender ?? '',
    birthday: character.birthday, deity: character.deity,
    gear: character.gear.map(item => ({
      ...item, stains: item.stains.flatMap(stain => stain ? [stain] : []),
      candidates: item.candidates.map(candidate => ({
        id: candidate.id, name: candidate.name, gameIcon: candidate.icon,
        category: candidate.details?.category, rarity: candidate.details?.rarity,
        item_level: candidate.details?.itemLevel, equip_level: candidate.details?.equipLevel,
        job_category: candidate.details?.jobCategory, physical_damage: candidate.details?.physicalDamage,
        magic_damage: candidate.details?.magicalDamage, physical_defense: candidate.details?.physicalDefense,
        magic_defense: candidate.details?.magicalDefense, attributes: candidate.details?.attributes,
      })),
    })),
    game: { portraitRoot: character.portraitRoot, appearance: Boolean(character.race && character.gender),
      jobId: character.classJobId, level: character.level, detailError: character.detailError },
  }
}

function avatar(character: Character) {
  return character.game ? character.game.portraitRoot + 'portrait.png' : portrait(character.id, 'portrait')
}

function updateAvatar(event: Event) {
  if (event.currentTarget instanceof HTMLImageElement) event.currentTarget.hidden = event.type === 'error'
}

function go(next: Screen) {
  screen.value = next
  window.scrollTo(0, 0)
}

function authorized(result: GameResponse) {
  game.value = result
  status.value = result.notice ?? ''
  go(result.current ? 'profile' : 'choose')
  clearTimeout(poll)
  const current = ++generation
  attemptedRole = undefined
  community.value = undefined
  void pollCommunity(current)
}

async function pollCommunity(token: number) {
  try {
    const snapshot = await getCommunityStatus()
    if (token !== generation) return
    if (snapshot) {
      community.value = snapshot
      const id = gameCharacter.value?.id
      if (id && attemptedRole !== id && (snapshot.state === 'connected' || snapshot.state === 'unbound'))
        void syncCommunity()
    }
  } catch {
    // Lobby and its cached profile remain available independently.
  }
  if (token === generation && game.value) poll = setTimeout(() => pollCommunity(token), 1000)
}

async function syncCommunity() {
  const id = gameCharacter.value?.id
  if (!id || syncingCommunity) return
  const token = generation
  attemptedRole = id
  syncingCommunity = true
  try {
    await selectCommunityCharacter(id)
  } catch {
    // The profile can still use cached game data when community synchronization fails.
  } finally {
    if (token === generation) syncingCommunity = false
  }
}

async function choose(character: Character) {
  if (!game.value) {
    activeId.value = Number(character.id)
    localStorage.setItem(activeKey, JSON.stringify(character.id))
    go('profile')
    return
  }
  if (choosing.value || lobbyBusy.value) return
  const token = generation
  choosing.value = true
  status.value = ''
  try {
    const result = await chooseGameCharacter(String(character.id))
    if (token !== generation) return
    if (result.error || !result.current) {
      status.value = result.error ?? '无法选择角色'
      return
    }
    game.value = result
    attemptedRole = undefined
    go('profile')
    if (community.value?.state === 'connected' || community.value?.state === 'unbound') void syncCommunity()
  } catch {
    if (token === generation) status.value = '无法选择角色'
  } finally {
    choosing.value = false
  }
}

async function readLobby(index: number, refresh: boolean) {
  if (lobbyBusy.value || choosing.value) return
  const token = generation
  lobbyBusy.value = true
  status.value = ''
  try {
    const id = crypto.randomUUID()
    const result = refresh ? await refreshLobby(id, index) : await selectServiceAccount(id, index)
    if (token !== generation) return
    if (result.error) {
      status.value = result.error
      return
    }
    game.value = result
    if (screen.value === 'settings') go(result.current ? 'profile' : 'choose')
  } catch {
    if (token === generation) status.value = '无法读取角色信息'
  } finally {
    lobbyBusy.value = false
  }
}

async function logout() {
  generation++
  clearTimeout(poll)
  game.value = undefined
  community.value = undefined
  syncingCommunity = false
  status.value = ''
  go('login')
  try {
    if (!await signOutGame()) status.value = '无法清除本机保存的登录状态'
  } catch {
    status.value = '无法确认游戏会话已清理'
  }
}

function exitDemo() {
  localStorage.removeItem(activeKey)
  activeId.value = null
  if (previewPage) history.replaceState(null, '', location.pathname)
  go('login')
}

function backFromChoice() {
  status.value = ''
  if (profile.value) go('settings')
  else if (game.value) void logout()
  else go('login')
}

onMounted(async () => {
  if (screen.value === 'login' && window.HybridWebView) {
    void getSavedGameArea().then(saved => {
      if (saved !== null && screen.value === 'login') go('game')
    }).catch(() => {})
  }
  try {
    const response = await fetch(asset('roster.json'))
    if (!response.ok) throw new Error()
    demos.value = await response.json() as Character[]
    if (previewPage && !profile.value && cover.value) void choose(cover.value)
    else if (screen.value === 'profile' && !profile.value) go('choose')
  } catch {
    loadError.value = '演示角色数据加载失败，请刷新页面。'
  }
})
onBeforeUnmount(() => { generation++; clearTimeout(poll) })
</script>

<template>
  <header class="app-header">
    <div class="header-content">
      <button class="brand" type="button" :aria-label="profile ? '返回角色主页' : '返回登录页'"
        @click="go(profile ? 'profile' : game ? 'choose' : 'login')">
        <span class="brand-mark"><UiIcon name="crystal" :size="21" /></span>
        <span>艾欧泽亚档案</span>
      </button>
      <nav v-if="profile" class="app-nav" aria-label="主导航">
        <button type="button" :aria-current="screen === 'profile' ? 'page' : undefined" @click="go('profile')">
          <UiIcon name="user" :size="17" />角色主页
        </button>
        <button type="button" :aria-current="screen === 'settings' || screen === 'choose' ? 'page' : undefined" @click="go('settings')">
          <UiIcon name="settings" :size="17" />设置
        </button>
      </nav>
      <span v-if="profile && !game" class="demo-label"><span></span>演示模式</span>
    </div>
  </header>

  <main v-if="screen === 'login'" class="landing">
    <div class="login-copy">
      <span class="brand-mark large-mark"><UiIcon name="crystal" :size="32" /></span>
      <h1>艾欧泽亚<br>角色档案</h1>
      <p>查看角色外观、档案与装备。</p>
      <div class="login-actions">
        <button type="button" class="primary" @click="go('game')">盛趣登录</button>
        <button type="button" class="secondary" :disabled="!demos.length" @click="go('choose')">进入演示</button>
      </div>
      <p v-if="status || loadError" role="status">{{ status || loadError }}</p>
    </div>
    <div class="login-art" aria-hidden="true">
      <img v-if="cover" :src="portrait(cover.id)" alt="">
    </div>
  </main>

  <main v-else-if="screen === 'choose'" class="narrow-page choose-page">
    <div class="page-heading">
      <div><h1>选择角色</h1><p>{{ list.length }} 个角色<span v-if="list[0]"> · {{ list[0].dc }}</span></p></div>
      <button type="button" class="secondary" @click="backFromChoice">{{ profile ? '返回设置' : '返回' }}</button>
    </div>
    <div v-if="game && (game.accounts.length > 1 || !game.cached)" class="account-choice">
      <label v-if="game.accounts.length" for="service-account">服务账号</label>
      <select v-if="game.accounts.length" id="service-account" :value="game.selectedIndex ?? ''" :disabled="lobbyBusy || choosing"
        @change="readLobby(Number(($event.target as HTMLSelectElement).value), false)">
        <option value="" disabled>请选择服务账号</option>
        <option v-for="entry in game.accounts" :key="entry.index" :value="entry.index">{{ entry.name || '服务账号 ' + (entry.index + 1) }}</option>
      </select>
      <button v-if="(!game.cached && game.selectedIndex !== null) || !game.accounts.length" class="secondary" type="button"
        :disabled="lobbyBusy" @click="readLobby(game.selectedIndex ?? -1, true)">
        {{ lobbyBusy ? '正在读取…' : '从 Lobby 读取角色' }}
      </button>
    </div>
    <p v-if="game?.cached && !list.length" role="status">该服务账号没有角色。</p>
    <p v-if="status" role="status">{{ status }}</p>
    <div class="choose-grid">
      <button v-for="character in list" :key="character.id" type="button" class="paper choose-card"
        :class="{ 'current-card': profile?.id === character.id }" :disabled="lobbyBusy || choosing" @click="choose(character)">
        <span class="character-avatar">
          <UiIcon v-if="character.game" name="user" :size="22" />
          <img :key="avatar(character)" :src="avatar(character)" alt="" hidden @load="updateAvatar" @error="updateAvatar">
        </span>
        <span><strong>{{ character.name }}</strong><small>{{ character.world }} · {{ character.race || '未获取' }}</small></span>
        <UiIcon v-if="profile?.id === character.id" class="active-check" name="check" :size="19" />
      </button>
    </div>
  </main>

  <main v-else-if="screen === 'profile' && profile" class="profile-page">
    <CharacterDetails :key="profileKey" :character="profile" />
  </main>

  <main v-else-if="screen === 'settings' && profile" class="narrow-page settings-page">
    <div class="page-heading"><div><h1>设置</h1></div></div>
    <section class="paper settings-panel" aria-labelledby="active-character">
      <div class="panel-title"><span class="section-icon"><UiIcon name="user" :size="18" /></span><h2 id="active-character">当前角色</h2></div>
      <div class="settings-character">
        <span class="character-avatar">
          <UiIcon v-if="profile.game" name="user" :size="22" />
          <img :key="avatar(profile)" :src="avatar(profile)" alt="" hidden @load="updateAvatar" @error="updateAvatar">
        </span>
        <div><strong>{{ profile.name }}</strong><small>{{ profile.dc }} · {{ profile.world }}</small></div>
        <button type="button" class="secondary switch-button" :disabled="lobbyBusy" @click="status = ''; go('choose')">
          <UiIcon name="swap" :size="17" />切换角色
        </button>
      </div>
      <div v-if="game" class="settings-actions">
        <button type="button" class="secondary" :disabled="lobbyBusy" @click="readLobby(game.currentIndex ?? -1, true)">
          {{ lobbyBusy ? '正在刷新角色信息…' : '刷新角色信息' }}
        </button>
        <p v-if="status" role="status">{{ status }}</p>
      </div>
    </section>
    <GameVersionSettings />
    <button v-if="game" class="secondary exit-button" type="button" @click="logout">退出游戏登录</button>
    <button v-else class="secondary exit-button" type="button" @click="exitDemo">退出演示</button>
  </main>

  <GameLogin v-else-if="screen === 'game'" @back="go('login')" @authorized="authorized" />
  <main v-else class="error-screen" role="status">正在载入角色…</main>
</template>

<style scoped>
.account-choice, .settings-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; margin-block: 12px; }
.account-choice select { min-width: 12rem; padding: .45rem; border: 1px solid var(--line); border-radius: 6px; }
.character-avatar { display: grid; place-items: center; }
.character-avatar:has(img:not([hidden])) > svg { visibility: hidden; }
.character-avatar img[hidden] { display: none; }
</style>
