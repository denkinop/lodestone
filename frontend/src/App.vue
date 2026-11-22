<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import CharacterDetails from './CharacterDetails.vue'
import GameVersionSettings from './GameVersionSettings.vue'
import GameLogin from './GameLogin.vue'
import HomePage from './HomePage.vue'
import UiIcon from './UiIcon.vue'
import WeatherPage from './WeatherPage.vue'
import type { Character } from './character'
import { communityJobProgress, lobbyJobName } from './jobs'
import {
  beginGameLogin, cancelGameOperation, chooseGameCharacter, getCommunityStatus, getSavedGameArea, refreshLobby, selectCommunityCharacter,
  selectServiceAccount, signOutGame, type CommunityStatus, type GameCharacter, type GameResponse,
} from './bridge'

type Screen = 'home' | 'choose' | 'profile' | 'weather' | 'settings'
const screen = ref<Screen>('home')
const game = ref<GameResponse>()
const community = ref<CommunityStatus>()
const status = ref('')
const choosing = ref(false)
const lobbyBusy = ref(false)
const signingOut = ref(false)
const restoring = ref(true)
const showLogin = ref(false)
const preparingPortrait = ref<string>()
const currentAvatar = ref<{ root: string; source: string }>()
let attemptedPortrait: string | undefined
let restoreRequest: string | undefined
let poll: ReturnType<typeof setTimeout> | undefined
let generation = 0
let communityRetry: { id: string; attempts: number; after: number } | undefined
let syncingCommunity = false

const gameCharacter = computed(() => game.value?.current)
const characterPrompt = computed(() => restoring.value ? '正在恢复已保存的登录…'
  : signingOut.value ? '正在退出登录…'
    : game.value ? '请在设置中选择角色。' : '请在设置中登录后查看角色信息。')
const cachedProfile = computed(() => gameCharacter.value ? toProfile(gameCharacter.value) : undefined)
const communityJobs = computed(() => {
  const character = cachedProfile.value
  const jobs = community.value?.jobProfile
  if (character && jobs?.character === character.name && jobs.area === character.dc && jobs.world === character.world)
    return communityJobProgress(jobs.levels, character.jobId)
})
const profile = computed(() => {
  const cached = cachedProfile.value
  return cached && communityJobs.value ? { ...cached, jobProgress: communityJobs.value } : cached
})
const list = computed(() => game.value?.characters.map(toProfile) ?? [])
const profileKey = computed(() => {
  const character = gameCharacter.value
  return character ? `${character.id}:${character.revision}` : undefined
})

watch(() => gameCharacter.value?.portraitRoot, () => {
  attemptedPortrait = undefined
  preparingPortrait.value = undefined
  currentAvatar.value = undefined
})

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
    portraitRoot: character.portraitRoot, appearance: Boolean(character.race && character.gender),
    jobId: character.classJobId, level: character.level, detailError: character.detailError,
  }
}

function avatar(character: Character) {
  return currentAvatar.value?.root === character.portraitRoot
    ? currentAvatar.value.source : character.portraitRoot + 'portrait.png'
}

function updateAvatar(event: Event) {
  if (event.currentTarget instanceof HTMLImageElement) event.currentTarget.hidden = event.type === 'error'
}

function prepareAvatar(event: Event, character: Character) {
  updateAvatar(event)
  if (character.appearance && attemptedPortrait !== character.portraitRoot)
    startPortrait(character.portraitRoot)
}

function startPortrait(root: string) {
  if (gameCharacter.value?.portraitRoot !== root) return
  attemptedPortrait = root
  preparingPortrait.value = root
}

function showPortrait(root: string, source: string) {
  if (gameCharacter.value?.portraitRoot === root) currentAvatar.value = { root, source }
}

function finishPortrait(root: string) {
  if (preparingPortrait.value === root) preparingPortrait.value = undefined
}

function go(next: Screen) {
  screen.value = next
  window.scrollTo(0, 0)
}

function authorized(result: GameResponse) {
  game.value = result
  status.value = result.notice ?? ''
  clearTimeout(poll)
  const current = ++generation
  communityRetry = undefined
  community.value = undefined
  void pollCommunity(current)
}

function signedIn(result: GameResponse) {
  showLogin.value = false
  authorized(result)
  if (!result.current && screen.value === 'settings') go('choose')
}

async function restoreLogin() {
  const token = generation
  try {
    const area = await getSavedGameArea()
    if (area === null || token !== generation) return
    restoreRequest = crypto.randomUUID()
    const result = await beginGameLogin(restoreRequest, 'saved', '', area, false)
    if (token !== generation) return
    if (result.error) status.value = result.error
    else authorized(result)
  } catch {
    if (token === generation && restoreRequest) status.value = '无法打开已保存的角色信息'
  } finally {
    restoreRequest = undefined
    restoring.value = false
  }
}

async function pollCommunity(token: number) {
  try {
    const snapshot = await getCommunityStatus()
    if (token !== generation) return
    if (snapshot) {
      community.value = snapshot
      void syncCommunity()
    }
  } catch {
    // Lobby and its cached profile remain available independently.
  }
  if (token === generation && game.value) poll = setTimeout(() => pollCommunity(token), 1000)
}

async function syncCommunity() {
  const id = gameCharacter.value?.id
  const session = community.value
  if (!id || syncingCommunity || communityJobs.value || session?.jobsStatus === 'loading'
    || (session?.state !== 'connected' && session?.state !== 'unbound')) return
  if (communityRetry?.id !== id) communityRetry = { id, attempts: 0, after: 0 }
  const retry = communityRetry
  if (retry.attempts >= 4 || performance.now() < retry.after) return
  const token = generation
  retry.attempts++
  syncingCommunity = true
  try {
    await selectCommunityCharacter(id)
  } catch {
    // The profile can still use cached game data when community synchronization fails.
  } finally {
    if (token === generation) {
      retry.after = performance.now() + 1000 * 2 ** (retry.attempts - 1)
      syncingCommunity = false
    }
  }
}

async function choose(character: Character) {
  if (choosing.value || lobbyBusy.value || signingOut.value) return
  const token = generation
  choosing.value = true
  status.value = ''
  try {
    const result = await chooseGameCharacter(character.id)
    if (token !== generation) return
    if (result.error || !result.current) {
      status.value = result.error ?? '无法选择角色'
      return
    }
    game.value = result
    communityRetry = undefined
    if (screen.value === 'choose') go('profile')
    void syncCommunity()
  } catch {
    if (token === generation) status.value = '无法选择角色'
  } finally {
    choosing.value = false
  }
}

async function readLobby(index: number, refresh: boolean) {
  if (lobbyBusy.value || choosing.value || signingOut.value) return
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
    status.value = result.notice ?? ''
    if (screen.value === 'settings') go(result.current ? 'profile' : 'choose')
  } catch {
    if (token === generation) status.value = '无法读取角色信息'
  } finally {
    lobbyBusy.value = false
  }
}

async function logout() {
  if (signingOut.value) return
  signingOut.value = true
  generation++
  clearTimeout(poll)
  syncingCommunity = false
  status.value = ''
  game.value = undefined
  community.value = undefined
  try {
    if (!await signOutGame()) status.value = '无法清除本机保存的登录状态'
  } catch {
    status.value = '无法确认本机登录状态已清除'
  } finally {
    signingOut.value = false
  }
}

function backFromChoice() {
  status.value = ''
  go('settings')
}

onMounted(() => { void restoreLogin() })
onBeforeUnmount(() => {
  generation++
  clearTimeout(poll)
  if (restoreRequest) void cancelGameOperation(restoreRequest).catch(() => {})
})
</script>

<template>
  <header class="app-header">
    <div class="header-content">
      <button class="brand" type="button" aria-label="掌上艾欧泽亚，返回首页" @click="go('home')">
        <span class="brand-mark"><UiIcon name="crystal" :size="21" /></span>
        <span>掌上艾欧泽亚</span>
      </button>
      <nav class="app-nav" aria-label="主导航">
        <button type="button" :aria-current="screen === 'home' ? 'page' : undefined" @click="go('home')">
          <UiIcon name="home" :size="18" />首页
        </button>
        <button type="button" :aria-current="screen === 'profile' ? 'page' : undefined" @click="go('profile')">
          <UiIcon name="user" :size="18" />角色
        </button>
        <button type="button" :aria-current="screen === 'weather' ? 'page' : undefined" @click="go('weather')">
          <UiIcon name="weather" :size="18" />天气
        </button>
        <button type="button" :aria-current="screen === 'settings' || screen === 'choose' ? 'page' : undefined" @click="go('settings')">
          <UiIcon name="settings" :size="18" />设置
        </button>
      </nav>
    </div>
  </header>

  <HomePage v-show="screen === 'home'">
    <template #character>
      <section class="home-card home-character-panel" aria-labelledby="home-character">
        <div class="home-card-heading">
          <span class="home-card-icon"><UiIcon name="user" :size="19" /></span><h2 id="home-character">我的角色</h2>
        </div>
        <div v-if="profile" class="home-character-row">
          <span class="character-avatar">
            <UiIcon name="user" :size="20" />
            <img :key="avatar(profile)" :src="avatar(profile)" alt="" hidden @load="updateAvatar" @error="prepareAvatar($event, profile)">
          </span>
          <div>
            <strong>{{ profile.name }}</strong>
            <small>{{ profile.dc }} · {{ profile.world }}</small>
            <small v-if="profile.jobId || profile.level">{{ lobbyJobName(profile.jobId) }}<template v-if="profile.level"> · {{ profile.level }} 级</template></small>
          </div>
          <button type="button" class="home-text-button" @click="go('profile')">查看信息<UiIcon name="right" :size="15" /></button>
        </div>
        <div v-else class="profile-prompt">
          <p role="status">{{ characterPrompt }}</p>
          <button v-if="!restoring && !signingOut" type="button" class="home-text-button" @click="go('settings')">
            前往设置<UiIcon name="right" :size="15" />
          </button>
        </div>
      </section>
    </template>
  </HomePage>

  <KeepAlive>
    <WeatherPage v-if="screen === 'weather'" />
  </KeepAlive>

  <main v-if="screen === 'choose' && game" class="narrow-page choose-page">
    <div class="page-heading">
      <div><h1>选择角色</h1><p>{{ list.length }} 个角色<span v-if="list[0]"> · {{ list[0].dc }}</span></p></div>
      <button type="button" class="secondary" :disabled="lobbyBusy || choosing" @click="backFromChoice">返回设置</button>
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
          <UiIcon name="user" :size="22" />
          <img :key="avatar(character)" :src="avatar(character)" alt="" hidden @load="updateAvatar" @error="updateAvatar">
        </span>
        <span><strong>{{ character.name }}</strong><small>{{ character.world }} · {{ character.race || '未获取' }}</small></span>
        <UiIcon v-if="profile?.id === character.id" class="active-check" name="check" :size="19" />
      </button>
    </div>
  </main>

  <main v-if="profile && (screen === 'profile' || preparingPortrait === profile.portraitRoot)"
    v-show="screen === 'profile'" class="profile-page">
    <CharacterDetails :key="profileKey" :character="profile" :active="screen === 'profile'"
      @portrait-start="startPortrait" @portrait-ready="showPortrait" @portrait-finish="finishPortrait" />
  </main>
  <main v-else-if="screen === 'profile'" class="narrow-page settings-page">
    <div class="page-heading"><h1>角色信息</h1></div>
    <section class="paper settings-panel">
      <div class="profile-prompt">
        <p role="status">{{ characterPrompt }}</p>
        <button v-if="!restoring && !signingOut" type="button" class="secondary" @click="go('settings')">前往设置</button>
      </div>
    </section>
  </main>

  <main v-if="screen === 'settings' || showLogin" v-show="screen === 'settings'" class="narrow-page settings-page">
    <div class="page-heading"><div><h1>设置</h1></div></div>
    <section class="paper settings-panel account-panel" aria-labelledby="account-heading">
      <div class="panel-title"><span class="section-icon"><UiIcon name="user" :size="18" /></span><h2 id="account-heading">账号绑定</h2></div>
      <section class="account-provider" aria-labelledby="sdo-provider">
        <div class="account-provider-row">
          <div class="account-provider-copy">
            <h3 id="sdo-provider">盛趣账号</h3>
            <p v-if="!showLogin" role="status">{{ game ? '已登录' : restoring || signingOut ? characterPrompt : '登录后即可查看和切换角色。' }}</p>
          </div>
          <button v-if="game" class="secondary" type="button" :disabled="lobbyBusy || choosing" @click="logout">退出登录</button>
          <button v-else-if="!showLogin" class="primary" type="button" :disabled="restoring || signingOut" @click="showLogin = true">登录</button>
        </div>
        <GameLogin v-if="showLogin" class="account-login" :logout-error="status" @authorized="signedIn">
          <template #actions>
            <button class="secondary" type="button" @click="showLogin = false">关闭登录</button>
          </template>
        </GameLogin>
        <p v-else-if="!game && status" class="account-status" role="status">{{ status }}</p>
      </section>
      <section class="account-provider" aria-labelledby="fflogs-provider" hidden>
        <div class="account-provider-row">
          <h3 id="fflogs-provider">FFLOGS</h3>
          <p>待接入</p>
        </div>
      </section>
    </section>
    <section v-if="game" class="paper settings-panel account-panel" aria-labelledby="active-character">
      <div class="panel-title"><span class="section-icon"><UiIcon name="user" :size="18" /></span><h2 id="active-character">当前角色</h2></div>
      <div class="settings-character">
        <template v-if="profile">
          <span class="character-avatar">
            <UiIcon name="user" :size="22" />
            <img :key="avatar(profile)" :src="avatar(profile)" alt="" hidden @load="updateAvatar" @error="updateAvatar">
          </span>
          <div><strong>{{ profile.name }}</strong><small>{{ profile.dc }} · {{ profile.world }}</small></div>
        </template>
        <p v-else>已登录，请选择角色。</p>
        <button type="button" class="secondary switch-button" :disabled="lobbyBusy || choosing" @click="status = ''; go('choose')">
          <UiIcon name="swap" :size="17" />{{ profile ? '切换角色' : '选择角色' }}
        </button>
      </div>
      <div v-if="profile" class="settings-actions">
        <button type="button" class="secondary" :disabled="lobbyBusy || choosing" @click="readLobby(game.currentIndex ?? -1, true)">
          {{ lobbyBusy ? '正在刷新角色信息…' : '刷新角色信息' }}
        </button>
      </div>
      <p v-if="status" class="account-status" role="status">{{ status }}</p>
    </section>
    <GameVersionSettings v-if="screen === 'settings'" />
  </main>
</template>

<style scoped>
.account-choice, .settings-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; margin-block: 12px; }
.account-choice select { min-width: 12rem; padding: .45rem; border: 1px solid var(--line); border-radius: 6px; }
.character-avatar { display: grid; place-items: center; }
.character-avatar > svg { color: #edf5ff; }
.character-avatar:has(img:not([hidden])) > svg { visibility: hidden; }
.character-avatar img[hidden] { display: none; }
.home-character-row .character-avatar { width: 42px; height: 42px; }
.account-provider + .account-provider { margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--line); }
.account-provider-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.account-provider-row h3 { font-size: 1rem; }
.account-provider-row p { color: var(--muted); font-size: .875rem; }
.account-provider-row button { flex-shrink: 0; }
.account-provider-copy p { margin-top: 4px; }
.account-login { margin-top: 12px; }
.account-status { margin-top: 12px; color: var(--muted); font-size: .875rem; }
</style>
