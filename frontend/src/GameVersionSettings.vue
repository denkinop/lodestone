<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  cancelGameVersion, getGameVersionProgress, getLobbyVersion, refreshGameVersion, setLobbyVersion,
  type VersionProgress,
} from './bridge'

defineProps<{ exdVersion?: string; exdStatus: string; exdUpdating: boolean }>()
defineEmits<{ updateExd: [] }>()

const lobby = ref<number | string>('')
const lobbyStatus = ref('')
const saving = ref(false)
const activeRequest = ref<string>()
const cancelling = ref(false)
const progress = ref<VersionProgress>()
const status = ref('')
const percentage = computed(() => progress.value
  ? Math.floor(100 * progress.value.bytesRead / progress.value.totalBytes) : 0)
let poll: ReturnType<typeof setTimeout> | undefined

onMounted(async () => {
  try {
    lobby.value = await getLobbyVersion()
  } catch {
    lobbyStatus.value = '无法读取大厅服务器版本'
  }
})

async function save() {
  saving.value = true
  lobbyStatus.value = ''
  try {
    if (typeof lobby.value !== 'number' || !Number.isInteger(lobby.value)
      || lobby.value < 0 || lobby.value > 65535 || !await setLobbyVersion(lobby.value)) {
      lobbyStatus.value = '请输入 0 至 65535 的整数'
      return
    }
    lobbyStatus.value = '大厅服务器版本已保存'
  } catch {
    lobbyStatus.value = '无法保存大厅服务器版本'
  } finally {
    saving.value = false
  }
}

function scheduleProgress(id: string) {
  poll = setTimeout(async () => {
    try {
      const value = await getGameVersionProgress(id)
      if (activeRequest.value === id && value) progress.value = value
    } catch {
      // The refresh response reports the outcome of the operation.
    }
    if (activeRequest.value === id) scheduleProgress(id)
  }, 350)
}

async function refresh() {
  if (activeRequest.value) return
  const id = crypto.randomUUID()
  activeRequest.value = id
  progress.value = undefined
  status.value = '正在检查官方修复清单…'
  scheduleProgress(id)
  try {
    const result = await refreshGameVersion(id)
    if (activeRequest.value !== id) return
    status.value = result.error ?? (
      '资源版本 ' + result.revision + (result.cached ? ' · 已验证缓存' : ' · 已生成版本信息')
    )
  } catch {
    if (activeRequest.value === id) status.value = '无法检查资源版本，请稍后重试'
  } finally {
    if (activeRequest.value === id) {
      clearTimeout(poll)
      activeRequest.value = undefined
      cancelling.value = false
    }
  }
}

async function cancel() {
  const id = activeRequest.value
  if (!id || cancelling.value) return
  cancelling.value = true
  try {
    await cancelGameVersion(id)
  } catch {
    if (activeRequest.value === id) {
      status.value = '无法取消，请等待当前检查结束'
      cancelling.value = false
    }
  }
}

onBeforeUnmount(() => {
  clearTimeout(poll)
  const id = activeRequest.value
  activeRequest.value = undefined
  if (id) void cancelGameVersion(id).catch(() => {})
})
</script>

<template>
  <section class="settings-group" aria-labelledby="version-title">
    <h2 id="version-title" class="settings-group-heading">高级设置</h2>
    <div class="paper">
      <form class="settings-row version-row" @submit.prevent="save">
        <label for="lobby-version">大厅服务器版本</label>
        <input id="lobby-version" v-model.number="lobby" type="number" min="0" max="65535" step="1" required>
        <button class="secondary" type="submit" :disabled="saving">保存</button>
        <span v-if="lobbyStatus" class="version-status" role="status">{{ lobbyStatus }}</span>
      </form>
      <div class="settings-row version-row">
        <button class="secondary" type="button" :disabled="Boolean(activeRequest)" @click="refresh">检查资源版本</button>
        <button v-if="activeRequest" class="secondary" type="button" :disabled="cancelling" @click="cancel">取消</button>
        <span v-if="!activeRequest || !progress" class="version-status" role="status">{{ status }}</span>
        <template v-else>
          <progress :aria-label="'正在读取游戏 EXE ' + percentage + '%'" :value="percentage" max="100" />
          <span class="version-status">正在读取游戏 EXE · {{ percentage }}%</span>
        </template>
      </div>
      <div class="settings-row version-row">
        <button class="secondary" type="button" :disabled="exdUpdating" @click="$emit('updateExd')">更新游戏数据</button>
        <span class="version-status" role="status">{{ exdStatus || (exdVersion ? '当前版本 ' + exdVersion : '') }}</span>
      </div>
    </div>
  </section>
</template>

<style scoped>
.version-row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.version-row label { margin-right: auto; font-size: .92rem; font-weight: 650; }
.version-row input { width: 5rem; min-height: 42px; padding: 7px 10px; border: 1px solid var(--line); border-radius: var(--control-radius); background: var(--surface); }
.version-row progress { width: 8rem; accent-color: var(--blue); }
.version-status { color: var(--muted); font-size: .85rem; overflow-wrap: anywhere; }
.version-status:empty { display: none; }
form.version-row .version-status { flex-basis: 100%; }
</style>
