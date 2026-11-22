<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import UiIcon from './UiIcon.vue'

type Destination = 'home' | 'profile' | 'wardrobe' | 'weather' | 'settings'

defineProps<{ current: Destination }>()
const emit = defineEmits<{ navigate: [destination: Destination] }>()

const sections = [
  {
    label: '导航',
    items: [
      { destination: 'home', label: '首页', icon: 'home' },
      { destination: 'profile', label: '角色', icon: 'user' },
      { destination: 'settings', label: '设置', icon: 'settings' },
    ],
  },
  { label: '工具', items: [
    { destination: 'weather', label: '天气', icon: 'weather' },
    { destination: 'wardrobe', label: '衣橱', icon: 'shirt' },
  ] },
] as const

const mobileViewport = window.matchMedia('(max-width: 760px)')
const mobile = ref(mobileViewport.matches)
const collapsed = ref(false)
const drawer = ref<HTMLDialogElement>()
const drawerOpen = ref(false)

function openDrawer() {
  drawer.value?.showModal()
  drawerOpen.value = true
}

function closeDrawer() {
  drawer.value?.close()
  drawerOpen.value = false
}

function syncViewport(event: MediaQueryListEvent) {
  if (!event.matches) closeDrawer()
  mobile.value = event.matches
}

function navigate(destination: Destination) {
  if (mobile.value) closeDrawer()
  emit('navigate', destination)
}

onMounted(() => mobileViewport.addEventListener('change', syncViewport))
onBeforeUnmount(() => mobileViewport.removeEventListener('change', syncViewport))
</script>

<template>
  <header v-if="mobile" class="mobile-header">
    <button type="button" class="sidebar-toggle" aria-label="打开菜单" aria-haspopup="dialog"
      :aria-expanded="drawerOpen" aria-controls="navigation-drawer" @click="openDrawer">
      <UiIcon name="sidebar" :size="21" />
    </button>
  </header>

  <dialog id="navigation-drawer" ref="drawer" class="app-navigation-drawer" aria-label="主菜单"
    @close="drawerOpen = drawer?.open ?? false" @click.self="closeDrawer"></dialog>

  <Teleport defer to="#navigation-drawer" :disabled="!mobile">
    <div class="app-sidebar" :class="{ 'is-collapsed': !mobile && collapsed }">
      <div class="sidebar-heading">
        <slot name="character" :compact="!mobile && collapsed" />
        <button v-if="mobile" type="button" class="sidebar-toggle" aria-label="关闭菜单" autofocus @click="closeDrawer">
          <UiIcon name="close" :size="21" />
        </button>
        <button v-else type="button" class="sidebar-toggle" :aria-label="collapsed ? '展开侧栏' : '收起侧栏'"
          :title="collapsed ? '展开侧栏' : '收起侧栏'" :aria-expanded="!collapsed" aria-controls="main-navigation"
          @click="collapsed = !collapsed">
          <UiIcon name="sidebar" :size="20" />
        </button>
      </div>

      <nav id="main-navigation" class="app-nav" aria-label="主导航">
        <section v-for="section in sections" :key="section.label" class="nav-section" :aria-label="section.label">
          <h2 class="sidebar-label">{{ section.label }}</h2>
          <button v-for="item in section.items" :key="item.destination" type="button" class="nav-item" :data-destination="item.destination"
            :aria-current="current === item.destination ? 'page' : undefined" :aria-label="item.label"
            :title="!mobile && collapsed ? item.label : undefined" @click="navigate(item.destination)">
            <span class="nav-item-icon"><UiIcon :name="item.icon" :size="17" /></span>
            <span class="sidebar-label">{{ item.label }}</span>
          </button>
        </section>
      </nav>
    </div>
  </Teleport>
</template>

<style scoped>
.app-sidebar {
  position: sticky;
  top: 14px;
  display: flex;
  flex-direction: column;
  width: 220px;
  height: calc(100dvh - 28px);
  margin: 14px 0 14px 14px;
  overflow: hidden;
  border: 1px solid #e1e4ea;
  border-radius: 24px;
  box-shadow: var(--panel-shadow);
}
.app-sidebar, .mobile-header { background: #ffffffe5; backdrop-filter: blur(18px); }
.sidebar-heading { margin: 10px 10px 3px; padding: 6px; border-radius: 15px; background: #f0f1f6; }
.sidebar-heading, .mobile-header {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
}
.sidebar-heading { justify-content: space-between; }
.sidebar-toggle, .nav-item {
  display: flex;
  align-items: center;
  border: 0;
  border-radius: var(--control-radius);
  background: transparent;
}
.sidebar-toggle {
  flex: none;
  justify-content: center;
  width: 42px;
  height: 42px;
  padding: 0;
  border-radius: 50%;
  color: var(--navy);
}
.sidebar-toggle:hover { background: var(--paper); }
.nav-item:hover { color: var(--ink); background: var(--surface); }
.app-nav { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; padding: 12px 10px; }
.nav-section { display: grid; gap: 4px; }
.nav-section + .nav-section { margin-top: 15px; }
.nav-section h2 { padding: 0 10px 3px; color: var(--muted); font-size: .75rem; font-weight: 650; }
.nav-item { gap: 10px; min-height: 44px; padding: 7px 10px; text-align: left; font-size: .9rem; font-weight: 600; }
.nav-item-icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 27px;
  height: 27px;
  border-radius: 8px;
  color: var(--blue-hover);
  background: var(--blue-light);
}
.nav-item[data-destination="profile"] .nav-item-icon { color: var(--violet); background: var(--violet-light); }
.nav-item[data-destination="settings"] .nav-item-icon { color: var(--muted); background: var(--surface); }
.nav-item[data-destination="weather"] .nav-item-icon { color: var(--weather-accent); background: var(--weather-tint); }
.nav-item[data-destination="wardrobe"] .nav-item-icon { color: var(--teal); background: var(--teal-light); }
.nav-item[aria-current="page"] {
  color: var(--blue-hover);
  background: var(--blue-light);
  font-weight: 750;
}
.is-collapsed { width: 68px; }
.is-collapsed .sidebar-heading { flex-direction: column; margin-inline: 7px; padding-inline: 3px; }
.is-collapsed .sidebar-label { display: none; }
.is-collapsed .nav-item { justify-content: center; padding-inline: 0; }
.is-collapsed .nav-section + .nav-section { margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--line); }

.mobile-header {
  position: sticky;
  z-index: 5;
  top: 0;
  padding: calc(5px + env(safe-area-inset-top)) max(12px, env(safe-area-inset-right)) 5px max(12px, env(safe-area-inset-left));
  border-bottom: 1px solid #dde1e7b3;
}
.mobile-header .sidebar-toggle { background: var(--paper); box-shadow: 0 1px 4px #24314916; }
.mobile-header .sidebar-toggle:hover { background: var(--surface); }
.app-navigation-drawer {
  position: fixed;
  inset: max(8px, env(safe-area-inset-top)) auto max(8px, env(safe-area-inset-bottom)) max(8px, env(safe-area-inset-left));
  width: min(272px, calc(100% - 52px));
  max-width: none;
  height: auto;
  max-height: none;
  margin: 0;
  padding: 0;
  overflow: visible;
  border: 0;
  color: var(--ink);
  background: transparent;
}
.app-navigation-drawer::backdrop { background: var(--scrim); }
.app-navigation-drawer .app-sidebar { position: static; width: 100%; height: 100%; margin: 0; }
@media (prefers-reduced-transparency: reduce) {
  .app-sidebar, .mobile-header { background: var(--paper); backdrop-filter: none; }
}
</style>
