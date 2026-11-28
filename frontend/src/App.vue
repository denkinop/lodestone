<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import AppNavigation from './AppNavigation.vue'
import CharacterDetails from './CharacterDetails.vue'
import CharacterSettingsPrompt from './CharacterSettingsPrompt.vue'
import DatabasePage from './DatabasePage.vue'
import GameVersionSettings from './GameVersionSettings.vue'
import GameLogin from './GameLogin.vue'
import HomePage from './HomePage.vue'
import MarketPage from './MarketPage.vue'
import PageHeading from './PageHeading.vue'
import UiIcon from './UiIcon.vue'
import WardrobePage from './WardrobePage.vue'
import WeatherPage from './WeatherPage.vue'
import type { Character } from './character'
import { communityJobProgress, lobbyJobName, type JobSyncNotice } from './jobs'
import {
  beginGameLogin, cancelGameOperation, chooseGameCharacter, getCommunityStatus, getCurrentGame, getExdProgress, getExdWeather,
  getSavedGameArea, refreshExd, refreshLobby, selectCommunityCharacter, selectServiceAccount, signOutGame,
  type CommunityStatus, type ExdWeatherView, type GameCharacter, type GameResponse, type WardrobeChange,
} from './bridge'

type Screen = 'home' | 'choose' | 'profile' | 'wardrobe' | 'weather' | 'market' | 'database' | 'settings'
const screen = ref<Screen>('home')
const game = ref<GameResponse>()
const community = ref<CommunityStatus>()
const status = ref('')
const choosing = ref(false)
const lobbyBusy = ref(false)
const signingOut = ref(false)
const restoring = ref(true)
const showLogin = ref(false)
const communityLoginDialog = ref<HTMLDialogElement>()
const preparingPortrait = ref<string>()
const currentAvatar = ref<{ root: string; source: string }>()
const exd = shallowRef<ExdWeatherView>()
const exdLoading = ref(false)
const exdUpdating = ref(false)
const exdStatus = ref('')
const exdGeneration = ref(0)
const wardrobeDraft = shallowRef<readonly WardrobeChange[]>([])
let attemptedPortrait: string | undefined
let restoreRequest: string | undefined
let poll: ReturnType<typeof setTimeout> | undefined
let exdPoll: ReturnType<typeof setTimeout> | undefined
let generation = 0
let communityRetry: { id: string; attempts: number; after: number } | undefined
const syncingCommunity = ref(false)

const gameCharacter = computed(() => game.value?.current)
const needsCommunityLogin = computed(() => community.value?.state === 'expired')
const navigationCharacterName = computed(() => gameCharacter.value?.name ?? (game.value ? '未选择角色' : '未登录'))
const characterPrompt = computed(() => restoring.value ? '正在恢复已保存的登录…'
  : signingOut.value ? '正在退出登录…'
    : game.value ? '请在设置中选择角色。' : '请在设置中登录后查看角色信息。')
const cachedProfile = computed(() => gameCharacter.value ? toProfile(gameCharacter.value) : undefined)
const communityJobProfile = computed(() => {
  const character = cachedProfile.value
  const jobs = community.value?.jobProfile
  if (character && jobs?.character === character.name && jobs.area === character.dc && jobs.world === character.world)
    return jobs
})
const communityJobs = computed(() => communityJobProfile.value
  ? communityJobProgress(communityJobProfile.value.levels, gameCharacter.value?.classJobId ?? null)
  : undefined)
const profile = computed(() => {
  const cached = cachedProfile.value
  return cached && communityJobs.value ? { ...cached, jobProgress: communityJobs.value } : cached
})
const jobSyncNotice = computed<JobSyncNotice | undefined>(() => {
  if (communityJobProfile.value) {
    const levels = communityJobs.value?.levels
    if (levels && Object.values(levels).some(level => level != null && level > 0)) return
    return { message: '已同步石之家，当前没有可显示的职业等级。' }
  }
  const session = community.value
  if (needsCommunityLogin.value)
    return { message: '石之家登录已失效，重新登录后可同步职业等级。', action: 'login' }
  if (syncingCommunity.value || session?.state === 'starting' || session?.jobsStatus === 'loading')
    return { message: '正在从石之家读取职业等级…' }
  if (session?.state === 'confirmation' || session?.state === 'identity')
    return { message: '石之家登录尚未验证，请重新登录以同步职业等级。', action: 'login' }
  return {
    message: '暂未从石之家读取到此角色的职业等级。',
    action: session?.state === 'connected' || session?.state === 'unbound' ? 'retry' : 'login',
  }
})
const list = computed(() => game.value?.characters.map(toProfile) ?? [])
const profileKey = computed(() => {
  const character = gameCharacter.value
  return character ? `${character.id}:${character.revision}:${exdGeneration.value}` : undefined
})

watch(profileKey, () => { wardrobeDraft.value = [] }, { flush: 'sync' })

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

async function loadExd() {
  if (exd.value || exdLoading.value) return
  exdLoading.value = true
  try {
    const data = await getExdWeather()
    exd.value ??= data
  } catch {
    // The weather page offers a retry and the update button can still install a fresh snapshot.
  } finally {
    exdLoading.value = false
  }
}

watch(screen, next => {
  if (next === 'weather' || next === 'settings') void loadExd()
})

function pollExd() {
  exdPoll = setTimeout(async () => {
    try {
      const progress = await getExdProgress()
      if (exdUpdating.value && progress) exdStatus.value = progress
    } catch {
      // The update response reports the outcome.
    }
    if (exdUpdating.value) pollExd()
  }, 750)
}

async function updateExd() {
  if (exdUpdating.value) return
  exdUpdating.value = true
  exdStatus.value = '正在连接 XIVAPI…'
  pollExd()
  try {
    const result = await refreshExd()
    if (!result.data || result.error) {
      exdStatus.value = result.error ?? '无法更新游戏数据，请稍后重试'
      return
    }
    exd.value = result.data
    let updateStatus = '已更新 · ' + result.data.version
    const displayedGame = game.value
    if (displayedGame) {
      try {
        const current = await getCurrentGame()
        if (current && game.value === displayedGame) {
          game.value = current
          exdGeneration.value++
        }
      } catch {
        updateStatus += ' · 重新读取当前角色后可查看最新资料'
      }
    }
    exdStatus.value = updateStatus
  } catch {
    exdStatus.value = '无法更新游戏数据，请稍后重试'
  } finally {
    exdUpdating.value = false
    clearTimeout(exdPoll)
  }
}

function authorized(result: GameResponse) {
  game.value = result
  status.value = result.notice ?? ''
  clearTimeout(poll)
  const current = ++generation
  communityRetry = undefined
  syncingCommunity.value = false
  community.value = undefined
  communityLoginDialog.value?.close()
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
      const previouslyExpired = needsCommunityLogin.value
      community.value = snapshot
      if (needsCommunityLogin.value && !previouslyExpired && !showLogin.value)
        communityLoginDialog.value?.showModal()
      void syncCommunity()
    }
  } catch {
    // Lobby and its cached profile remain available independently.
  }
  if (token === generation && game.value) poll = setTimeout(() => pollCommunity(token), 1000)
}

function startCommunityLogin() {
  communityLoginDialog.value?.close()
  status.value = ''
  go('settings')
  showLogin.value = true
}

function handleJobSync(action: NonNullable<JobSyncNotice['action']>) {
  if (action === 'login') {
    startCommunityLogin()
    return
  }
  communityRetry = undefined
  void syncCommunity()
}

async function syncCommunity() {
  const id = gameCharacter.value?.id
  const session = community.value
  if (!id || syncingCommunity.value || communityJobProfile.value || session?.jobsStatus === 'loading'
    || (session?.state !== 'connected' && session?.state !== 'unbound')) return
  if (communityRetry?.id !== id) communityRetry = { id, attempts: 0, after: 0 }
  const retry = communityRetry
  if (retry.attempts >= 4 || performance.now() < retry.after) return
  const token = generation
  retry.attempts++
  syncingCommunity.value = true
  try {
    await selectCommunityCharacter(id)
  } catch {
    // The profile can still use cached game data when community synchronization fails.
  } finally {
    if (token === generation) {
      retry.after = performance.now() + 1000 * 2 ** (retry.attempts - 1)
      syncingCommunity.value = false
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
  syncingCommunity.value = false
  status.value = ''
  game.value = undefined
  community.value = undefined
  communityLoginDialog.value?.close()
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
  exdUpdating.value = false
  clearTimeout(exdPoll)
  if (restoreRequest) void cancelGameOperation(restoreRequest).catch(() => {})
})
</script>

<template>
  <div class="app-shell">
    <AppNavigation :current="screen === 'choose' ? 'settings' : screen" @navigate="go">
      <template #character="{ compact }">
        <div class="navigation-character" :class="{ 'is-compact': compact }" :title="navigationCharacterName">
          <span class="character-avatar">
            <UiIcon name="user" :size="19" />
            <img v-if="profile" :key="avatar(profile)" :src="avatar(profile)" alt="" hidden
              @load="updateAvatar" @error="prepareAvatar($event, profile)">
          </span>
          <span class="navigation-character-name">{{ navigationCharacterName }}</span>
        </div>
      </template>
    </AppNavigation>
    <div class="app-content">
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
        <WeatherPage v-if="screen === 'weather' && exd" :catalog="exd.catalog" />
      </KeepAlive>
      <main v-if="screen === 'weather' && !exd" class="weather-page">
        <PageHeading title="天气查询" />
        <section class="paper settings-row">
          <span role="status">{{ exdLoading ? '正在读取天气数据…' : '无法读取天气数据' }}</span>
          <button v-if="!exdLoading" class="secondary" type="button" @click="loadExd">重试</button>
        </section>
      </main>

      <WardrobePage v-if="screen === 'wardrobe'" :key="profileKey" :character="gameCharacter"
        :prompt="restoring || signingOut ? characterPrompt : game ? '请在设置中选择角色。' : '请在设置中登录后选择角色。'"
        :can-open-settings="!restoring && !signingOut" :draft="wardrobeDraft"
        @settings="go('settings')" @draft="wardrobeDraft = $event" />

      <MarketPage v-if="screen === 'market'" :key="gameCharacter?.id" :character="gameCharacter" />
      <DatabasePage v-if="screen === 'database'" />

      <main v-if="screen === 'choose' && game" class="choose-page">
        <PageHeading title="选择角色">
          <button type="button" class="secondary" :disabled="lobbyBusy || choosing" @click="backFromChoice">返回设置</button>
        </PageHeading>
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
        <div v-if="list.length" class="paper character-list" role="group" aria-label="角色列表">
          <button v-for="character in list" :key="character.id" type="button" class="choose-card"
            :aria-current="profile?.id === character.id ? 'true' : undefined"
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

      <main v-if="screen === 'profile' || (profile && preparingPortrait === profile.portraitRoot)"
        v-show="screen === 'profile'" class="profile-page">
        <PageHeading title="角色信息" />
        <div v-if="profile" class="profile-layout">
          <CharacterDetails :key="profileKey" :character="profile" :active="screen === 'profile'"
            :job-sync-notice="jobSyncNotice" @job-sync="handleJobSync"
            @portrait-start="startPortrait" @portrait-ready="showPortrait" @portrait-finish="finishPortrait" />
        </div>
        <CharacterSettingsPrompt v-else :message="characterPrompt" :can-open-settings="!restoring && !signingOut"
          @settings="go('settings')" />
      </main>

      <main v-if="screen === 'settings' || showLogin" v-show="screen === 'settings'" class="settings-page">
        <PageHeading title="设置" />
        <section class="settings-group" aria-labelledby="account-heading">
          <h2 id="account-heading" class="settings-group-heading">账号绑定</h2>
          <div class="paper">
            <section aria-labelledby="sdo-provider">
              <div class="settings-row account-provider-row">
                <span class="section-icon"><UiIcon name="user" :size="18" /></span>
                <div class="account-provider-copy">
                  <h3 id="sdo-provider">盛趣账号</h3>
                  <p v-if="!showLogin" role="status">{{ game ? '已登录' : restoring || signingOut ? characterPrompt : '登录后即可查看和切换角色。' }}</p>
                </div>
                <button v-if="game && !showLogin" class="secondary" type="button" :disabled="lobbyBusy || choosing" @click="logout">退出登录</button>
                <button v-else-if="!showLogin" class="primary" type="button" :disabled="restoring || signingOut" @click="showLogin = true">登录</button>
              </div>
              <GameLogin v-if="showLogin" class="settings-row" :logout-error="status" :renewing="Boolean(game)" @authorized="signedIn">
                <template #actions>
                  <button class="secondary" type="button" @click="showLogin = false">关闭登录</button>
                </template>
              </GameLogin>
              <p v-else-if="!game && status" class="settings-row account-status" role="status">{{ status }}</p>
            </section>
            <section v-if="needsCommunityLogin && !showLogin" class="settings-row account-provider-row" aria-labelledby="community-provider">
              <div class="account-provider-copy">
                <h3 id="community-provider">石之家</h3>
                <p>登录已失效</p>
              </div>
              <button class="secondary" type="button" :disabled="lobbyBusy || choosing" @click="startCommunityLogin">重新登录</button>
            </section>
            <section class="settings-row" aria-labelledby="fflogs-provider" hidden>
              <div class="account-provider-row">
                <h3 id="fflogs-provider">FFLOGS</h3>
                <p>待接入</p>
              </div>
            </section>
          </div>
        </section>
        <section v-if="game" class="settings-group" aria-labelledby="active-character">
          <h2 id="active-character" class="settings-group-heading">当前角色</h2>
          <div class="paper">
            <div class="settings-row settings-character">
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
            <div v-if="profile" class="settings-row">
              <button type="button" class="secondary" :disabled="lobbyBusy || choosing" @click="readLobby(game.currentIndex ?? -1, true)">
                {{ lobbyBusy ? '正在刷新角色信息…' : '刷新角色信息' }}
              </button>
            </div>
            <p v-if="status" class="settings-row account-status" role="status">{{ status }}</p>
          </div>
        </section>
        <GameVersionSettings v-if="screen === 'settings'" :exd-version="exd?.version"
          :exd-status="exdStatus" :exd-updating="exdUpdating" @update-exd="updateExd" />
      </main>
    </div>
  </div>
  <dialog ref="communityLoginDialog" class="community-login-dialog" role="alertdialog"
    aria-labelledby="community-login-title" aria-describedby="community-login-description">
    <h2 id="community-login-title">石之家登录已失效</h2>
    <p id="community-login-description">可能是因为你在其他设备登录了石之家。是否重新登录以继续同步职业等级？</p>
    <form method="dialog">
      <button class="secondary" type="submit" autofocus>稍后</button>
      <button class="primary" type="button" :disabled="lobbyBusy || choosing || signingOut" @click="startCommunityLogin">重新登录</button>
    </form>
  </dialog>
</template>

<style scoped>
.account-choice { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; margin-block: 12px; }
.account-choice select { min-width: 12rem; min-height: 40px; padding: .45rem; border: 1px solid var(--line); border-radius: var(--control-radius); background: var(--paper); }
.character-avatar { display: grid; place-items: center; }
.character-avatar > svg { color: var(--violet); }
.character-avatar:has(img:not([hidden])) > svg { visibility: hidden; }
.character-avatar img[hidden] { display: none; }
.navigation-character { display: flex; align-items: center; min-width: 0; gap: 9px; padding: 4px; }
.navigation-character .character-avatar { width: 34px; height: 34px; border-radius: 50%; }
.navigation-character-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: .92rem; font-weight: 760; }
.navigation-character.is-compact .navigation-character-name { position: absolute; width: 1px; height: 1px; clip-path: inset(50%); }
.home-character-row .character-avatar { width: 42px; height: 42px; }
.account-provider-row { display: flex; align-items: center; gap: 12px; }
.account-provider-row h3 { font-size: 1rem; }
.account-provider-row p { color: var(--muted); font-size: .875rem; }
.account-provider-row .section-icon, .account-provider-row button { flex: none; }
.account-provider-row button { margin-left: auto; }
.account-provider-copy p { margin-top: 4px; }
.account-status { color: var(--muted); font-size: .875rem; }
.community-login-dialog {
  width: min(420px, calc(100% - 26px)); padding: 20px; color: var(--ink); background: var(--paper);
  border: 1px solid var(--line); border-radius: var(--panel-radius); box-shadow: var(--panel-shadow);
}
.community-login-dialog::backdrop { background: var(--scrim); }
.community-login-dialog h2 { color: var(--navy); font-size: 1.12rem; }
.community-login-dialog p { margin-block: 8px 18px; color: var(--muted); line-height: 1.55; }
.community-login-dialog form { display: flex; justify-content: flex-end; gap: 8px; }
</style>
