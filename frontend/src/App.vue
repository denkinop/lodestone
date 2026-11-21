<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import CharacterDetails from './CharacterDetails.vue'
import GameVersionSettings from './GameVersionSettings.vue'
import GameLogin from './GameLogin.vue'
import UiIcon from './UiIcon.vue'
import type { Character } from './character'
import { communityJobProgress } from './jobs'
import {
  chooseGameCharacter, getCommunityStatus, refreshLobby, selectCommunityCharacter,
  selectServiceAccount, signOutGame, type CommunityStatus, type GameCharacter, type GameResponse,
} from './bridge'

type Screen = 'login' | 'choose' | 'profile' | 'settings'
const screen = ref<Screen>('login')
const game = ref<GameResponse>()
const community = ref<CommunityStatus>()
const status = ref('')
const choosing = ref(false)
const lobbyBusy = ref(false)
const signingOut = ref(false)
let poll: ReturnType<typeof setTimeout> | undefined
let generation = 0
let communityRetry: { id: string; attempts: number; after: number } | undefined
let syncingCommunity = false

const gameCharacter = computed(() => game.value?.current)
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
  return character.portraitRoot + 'portrait.png'
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
  communityRetry = undefined
  community.value = undefined
  void pollCommunity(current)
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
    go('profile')
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
  try {
    if (!await signOutGame()) status.value = '无法清除本机保存的登录状态'
  } catch {
    status.value = '无法确认本机登录状态已清除'
  } finally {
    signingOut.value = false
    game.value = undefined
    community.value = undefined
    go('login')
  }
}

function backFromChoice() {
  status.value = ''
  if (profile.value) go('settings')
  else void logout()
}

onBeforeUnmount(() => { generation++; clearTimeout(poll) })
</script>

<template>
  <header class="app-header">
    <div class="header-content">
      <component :is="game ? 'button' : 'div'" class="brand" :type="game ? 'button' : undefined"
        :aria-label="profile ? '返回角色主页' : undefined" @click="game && go(profile ? 'profile' : 'choose')">
        <span class="brand-mark"><UiIcon name="crystal" :size="21" /></span>
        <span>艾欧泽亚档案</span>
      </component>
      <nav v-if="profile" class="app-nav" aria-label="主导航">
        <button type="button" :aria-current="screen === 'profile' ? 'page' : undefined" @click="go('profile')">
          <UiIcon name="user" :size="17" />角色主页
        </button>
        <button type="button" :aria-current="screen === 'settings' || screen === 'choose' ? 'page' : undefined" @click="go('settings')">
          <UiIcon name="settings" :size="17" />设置
        </button>
      </nav>
    </div>
  </header>

  <GameLogin v-if="screen === 'login'" :logout-error="status" @authorized="authorized" />

  <main v-else-if="screen === 'choose'" class="narrow-page choose-page">
    <div class="page-heading">
      <div><h1>选择角色</h1><p>{{ list.length }} 个角色<span v-if="list[0]"> · {{ list[0].dc }}</span></p></div>
      <button type="button" class="secondary" :disabled="signingOut" @click="backFromChoice">{{ profile ? '返回设置' : '返回登录' }}</button>
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

  <main v-else-if="screen === 'profile' && profile" class="profile-page">
    <CharacterDetails :key="profileKey" :character="profile" />
  </main>

  <main v-else-if="screen === 'settings' && profile" class="narrow-page settings-page">
    <div class="page-heading"><div><h1>设置</h1></div></div>
    <section class="paper settings-panel" aria-labelledby="active-character">
      <div class="panel-title"><span class="section-icon"><UiIcon name="user" :size="18" /></span><h2 id="active-character">当前角色</h2></div>
      <div class="settings-character">
        <span class="character-avatar">
          <UiIcon name="user" :size="22" />
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
    <button class="secondary exit-button" type="button" :disabled="signingOut" @click="logout">退出登录</button>
  </main>

  <main v-else class="error-screen" role="status">正在载入角色…</main>
</template>

<style scoped>
.account-choice, .settings-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; margin-block: 12px; }
.account-choice select { min-width: 12rem; padding: .45rem; border: 1px solid var(--line); border-radius: 6px; }
.character-avatar { display: grid; place-items: center; }
.character-avatar:has(img:not([hidden])) > svg { visibility: hidden; }
.character-avatar img[hidden] { display: none; }
</style>
