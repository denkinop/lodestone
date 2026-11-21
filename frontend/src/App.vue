<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import CharacterDetails from './CharacterDetails.vue'
import GameVersionSettings from './GameVersionSettings.vue'
import UiIcon from './UiIcon.vue'
import { asset, portrait, type Character } from './demo'

type Screen = 'login' | 'choose' | 'profile' | 'settings'
const activeKey = 'lodestone-demo-character'

function read<T>(key: string, fallback: T): T {
  try { return JSON.parse(localStorage.getItem(key) ?? 'null') ?? fallback } catch { return fallback }
}

const activeId = ref(read<number | null>(activeKey, null))
const previewPage = new URLSearchParams(location.search).get('preview') === 'profile'
const screen = ref<Screen>(activeId.value !== null || previewPage ? 'profile' : 'login')
const roster = ref<Character[]>([])
const current = computed(() => roster.value.find(character => character.id === activeId.value))
const cover = computed(() => roster.value[0])
const loadError = ref('')

function go(next: Screen) {
  screen.value = next
  window.scrollTo(0, 0)
}

function choose(character: Character) {
  activeId.value = character.id
  localStorage.setItem(activeKey, JSON.stringify(character.id))
  go('profile')
}

function exitDemo() {
  localStorage.removeItem(activeKey)
  activeId.value = null
  if (previewPage) history.replaceState(null, '', location.pathname)
  go('login')
}

onMounted(async () => {
  try {
    const response = await fetch(asset('roster.json'))
    if (!response.ok) throw new Error()
    roster.value = await response.json() as Character[]
    if (previewPage && !current.value && cover.value) choose(cover.value)
    else if (screen.value === 'profile' && !current.value) go('choose')
  } catch {
    loadError.value = '演示角色数据加载失败，请刷新页面。'
  }
})
</script>

<template>
  <header class="app-header">
    <div class="header-content">
      <button class="brand" type="button" :aria-label="current ? '返回角色主页' : '返回登录页'" @click="go(current ? 'profile' : 'login')">
        <span class="brand-mark"><UiIcon name="crystal" :size="21" /></span>
        <span>艾欧泽亚档案</span>
      </button>
      <nav class="app-nav" aria-label="主导航">
        <button v-if="current" type="button" :aria-current="screen === 'profile' ? 'page' : undefined" @click="go('profile')">
          <UiIcon name="user" :size="17" />角色主页
        </button>
        <button type="button" :aria-current="screen === 'settings' || screen === 'choose' ? 'page' : undefined" @click="go('settings')">
          <UiIcon name="settings" :size="17" />设置
        </button>
      </nav>
      <span class="demo-label"><span></span>演示模式</span>
    </div>
  </header>

  <main v-if="loadError" class="error-screen" role="alert">{{ loadError }}</main>

  <main v-else-if="screen === 'login'" class="landing">
    <div class="login-copy">
      <span class="brand-mark large-mark"><UiIcon name="crystal" :size="32" /></span>
      <h1>艾欧泽亚<br>角色档案</h1>
      <p>查看角色外观、档案与装备。</p>
      <button type="button" class="primary" :disabled="!roster.length" @click="go('choose')">进入演示</button>
    </div>
    <div class="login-art" aria-hidden="true">
      <img v-if="cover" :src="portrait(cover.id)" alt="">
    </div>
  </main>

  <main v-else-if="screen === 'choose'" class="narrow-page choose-page">
    <div class="page-heading">
      <div><h1>选择角色</h1><p>{{ roster.length }} 个角色<span v-if="roster.length"> · {{ roster[0]?.dc }}</span></p></div>
      <button type="button" class="secondary" @click="go(current ? 'settings' : 'login')">{{ current ? '返回设置' : '返回' }}</button>
    </div>
    <div class="choose-grid">
      <button v-for="character in roster" :key="character.id" type="button" class="paper choose-card"
        :class="{ 'current-card': activeId === character.id }" @click="choose(character)">
        <span class="character-avatar"><img :src="portrait(character.id, 'portrait')" alt=""></span>
        <span><strong>{{ character.name }}</strong><small>{{ character.world }} · {{ character.race }}</small></span>
        <UiIcon v-if="activeId === character.id" class="active-check" name="check" :size="19" />
      </button>
    </div>
  </main>

  <main v-else-if="screen === 'profile' && current" class="profile-page">
    <CharacterDetails :key="current.id" :character="current" />
  </main>

  <main v-else-if="screen === 'settings'" class="narrow-page settings-page">
    <div class="page-heading"><div><h1>设置</h1></div></div>
    <section v-if="current" class="paper settings-panel" aria-labelledby="active-character">
      <div class="panel-title"><span class="section-icon"><UiIcon name="user" :size="18" /></span><h2 id="active-character">当前角色</h2></div>
      <div class="settings-character">
        <span class="character-avatar"><img :src="portrait(current.id, 'portrait')" alt=""></span>
        <div><strong>{{ current.name }}</strong><small>{{ current.dc }} · {{ current.world }}</small></div>
        <button type="button" class="secondary switch-button" @click="go('choose')"><UiIcon name="swap" :size="17" />切换角色</button>
      </div>
    </section>
    <GameVersionSettings />
    <button v-if="current" class="secondary exit-button" type="button" @click="exitDemo">退出演示</button>
  </main>

  <main v-else class="error-screen" role="status">正在载入角色…</main>
</template>
