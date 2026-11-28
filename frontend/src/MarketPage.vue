<script setup lang="ts">
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import ItemIcon from './ItemIcon.vue'
import PageHeading from './PageHeading.vue'
import UiIcon from './UiIcon.vue'
import { getMarketDataCenters, getMarketPrices, getRecentMarketItems, searchMarketItems,
  type GameCharacter, type MarketBoard, type MarketDataCenter, type MarketItem, type MarketRecentItem } from './bridge'

const props = defineProps<{ character?: GameCharacter | null }>()

const searchInput = ref<HTMLInputElement>()
const detailHeading = ref<HTMLElement>()
const query = ref('')
const searched = ref('')
const matches = shallowRef<MarketItem[]>([])
const searching = ref(false)
const searchError = ref('')
const dataCenters = shallowRef<MarketDataCenter[]>([])
const loadingLocations = ref(false)
const locationError = ref('')
const marketLocation = shallowRef<{ dataCenter: string; worldId: number }>()
const recent = shallowRef<MarketRecentItem[]>()
const recentLoading = ref(false)
const recentError = ref('')
const selected = shallowRef<MarketItem>()
const board = shallowRef<MarketBoard>()
const priceLoading = ref(false)
const priceError = ref('')
const tab = ref<'listings' | 'recentHistory'>('listings')
let searchRequest = 0
let recentRequest = 0
let priceRequest = 0

const characterWorldId = computed(() => props.character?.currentWorldId ?? 0)
const characterDataCenter = computed(() => dataCenters.value.find(center =>
  center.worlds.some(world => world.id === characterWorldId.value)))
const centersByRegion = computed(() => {
  const groups = new Map<string, MarketDataCenter[]>()
  for (const center of dataCenters.value) {
    const region = center.region || '其他'
    const group = groups.get(region)
    if (group) group.push(center)
    else groups.set(region, [center])
  }
  const preferred = characterDataCenter.value?.region || '中国'
  return [...groups].sort(([left], [right]) => Number(right === preferred) - Number(left === preferred))
})
const dataCenter = computed({
  get: () => marketLocation.value?.dataCenter ?? characterDataCenter.value?.name ?? '',
  set: dataCenter => { marketLocation.value = { dataCenter, worldId: 0 } },
})
const worldId = computed({
  get: () => marketLocation.value?.worldId ?? characterWorldId.value,
  set: worldId => { marketLocation.value = { dataCenter: dataCenter.value, worldId } },
})
const worlds = computed(() => dataCenters.value.find(center => center.name === dataCenter.value)?.worlds ?? [])
const selectedWorld = computed(() => worlds.value.find(world => world.id === worldId.value))
const dataCenterQuery = computed(() => worldId.value === 0 ? dataCenter.value || null : null)
const canQuery = computed(() => worldId.value > 0 || dataCenterQuery.value !== null)
const scope = computed(() => dataCenterQuery.value ?? selectedWorld.value?.name ?? props.character?.currentWorld ?? '服务器')
const prices = computed(() => board.value?.[tab.value] ?? [])
const formatNumber = new Intl.NumberFormat('zh-CN')
const formatDate = new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
const gil = (value: number) => formatNumber.format(value)

function showTime(ms: number) {
  const date = new Date(ms)
  return ms > 0 && Number.isFinite(date.getTime()) ? formatDate.format(date) : '时间未知'
}

async function loadLocations() {
  loadingLocations.value = true
  locationError.value = ''
  try {
    const result = await getMarketDataCenters()
    if (result.data?.length) dataCenters.value = result.data
    else locationError.value = result.error || '无法读取数据中心和服务器。'
  } catch {
    locationError.value = '无法读取数据中心和服务器。'
  } finally {
    loadingLocations.value = false
  }
}

async function loadRecent() {
  if (!canQuery.value) return
  const request = ++recentRequest
  recentLoading.value = true
  recentError.value = ''
  try {
    const result = await getRecentMarketItems(worldId.value, dataCenterQuery.value)
    if (request !== recentRequest) return
    recent.value = result.data ?? undefined
    recentError.value = result.error ?? ''
  } catch {
    if (request === recentRequest) recentError.value = '无法读取最近更新，请重试。'
  } finally {
    if (request === recentRequest) recentLoading.value = false
  }
}

async function loadPrices() {
  if (!selected.value || !canQuery.value) return
  const request = ++priceRequest
  board.value = undefined
  priceLoading.value = true
  priceError.value = ''
  try {
    const result = await getMarketPrices(worldId.value, dataCenterQuery.value, selected.value.id)
    if (request !== priceRequest) return
    board.value = result.data ?? undefined
    priceError.value = result.error ?? ''
  } catch {
    if (request === priceRequest) priceError.value = '无法读取市价，请重试。'
  } finally {
    if (request === priceRequest) priceLoading.value = false
  }
}

function ensureRecent() {
  if (!searched.value && !recent.value && !recentLoading.value && !recentError.value) void loadRecent()
}

function closeItem() {
  priceRequest++
  selected.value = undefined
  ensureRecent()
}

function openItem(item: MarketItem) {
  selected.value = item
  tab.value = 'listings'
  void loadPrices()
  void nextTick(() => detailHeading.value?.focus())
}

function clearSearch() {
  searchRequest++
  query.value = ''
  searched.value = ''
  searching.value = false
  closeItem()
  searchInput.value?.focus()
}

async function search() {
  const term = query.value.trim()
  if (!term || !canQuery.value) return
  const request = ++searchRequest
  searched.value = term
  closeItem()
  matches.value = []
  searchError.value = ''
  searching.value = true
  try {
    const items = await searchMarketItems(term)
    if (request !== searchRequest) return
    matches.value = items
    const normalized = term.normalize('NFKC')
    const exact = items.find(item => item.name === normalized)
    if (exact) openItem(exact)
    else if (items.length === 1 && /^#?\d[\d,]*$/.test(normalized) && items[0]) openItem(items[0])
  } catch {
    if (request === searchRequest) searchError.value = '无法搜索物品，请重试。'
  } finally {
    if (request === searchRequest) searching.value = false
  }
}

onMounted(loadLocations)
watch([worldId, dataCenterQuery], () => {
  recentRequest++
  priceRequest++
  recent.value = undefined
  recentLoading.value = false
  recentError.value = ''
  if (selected.value) void loadPrices()
  else ensureRecent()
}, { immediate: true })
</script>

<template>
  <main class="market-page">
    <PageHeading title="市价查询" />
    <form class="market-search paper" aria-label="搜索可交易道具" @submit.prevent="search">
      <UiIcon name="search" :size="22" />
      <input ref="searchInput" v-model="query" type="text" aria-label="物品名称或 ID"
        enterkeyhint="search" maxlength="80" placeholder="搜索可交易道具名称或 ID" autocomplete="off">
      <button v-if="query || searched || selected" class="market-clear" type="button" aria-label="清除查询" @click="clearSearch">
        <UiIcon name="close" :size="18" />
      </button>
      <button class="market-submit" type="submit" aria-label="搜索" :disabled="!canQuery || !query.trim() || searching">
        <UiIcon name="arrow" :size="21" />
      </button>
    </form>

    <div class="market-scopes" role="group" aria-label="查询范围">
      <label>数据中心
        <select v-model="dataCenter" :disabled="!dataCenters.length">
          <option value="" disabled>{{ loadingLocations ? '正在读取…' : '选择数据中心' }}</option>
          <optgroup v-for="[region, centers] in centersByRegion" :key="region" :label="region">
            <option v-for="center in centers" :key="center.name" :value="center.name">{{ center.name }}</option>
          </optgroup>
        </select>
      </label>
      <label>服务器
        <select v-model="worldId" :disabled="!worlds.length">
          <option :value="0" :disabled="!dataCenter">{{ dataCenter ? '全部服务器' : '选择服务器' }}</option>
          <option v-if="worldId > 0 && !selectedWorld" :value="worldId">{{ character?.currentWorld }}</option>
          <option v-for="world in worlds" :key="world.id" :value="world.id">{{ world.name }}</option>
        </select>
      </label>
      <button v-if="characterWorldId > 0 && worldId !== characterWorldId" class="secondary" type="button" @click="marketLocation = undefined">
        <UiIcon name="location" :size="16" />当前角色服务器
      </button>
    </div>
    <div v-if="locationError" class="market-location-error">
      <p role="status">{{ locationError }}</p>
      <button type="button" class="secondary" @click="loadLocations">重试</button>
    </div>

    <section v-if="!canQuery" class="paper market-card">
      <p class="market-status" role="status">选择数据中心或服务器后即可查询市价。</p>
    </section>
    <section v-else-if="selected" class="paper market-card" aria-labelledby="market-item-title">
      <button type="button" class="market-back" @click="closeItem">
        <UiIcon name="left" :size="16" />{{ searched ? '返回搜索结果' : '返回最近更新' }}
      </button>
      <div class="market-title market-detail-title">
        <ItemIcon :icon="selected.icon" />
        <div>
          <h2 id="market-item-title" ref="detailHeading" tabindex="-1">{{ selected.name }}</h2>
          <p>{{ scope }} · #{{ gil(selected.id) }}</p>
        </div>
        <button type="button" class="secondary" :disabled="priceLoading" @click="loadPrices">刷新</button>
      </div>
      <p v-if="priceLoading" class="market-status" role="status">正在读取市价…</p>
      <p v-else-if="priceError" class="market-status" role="status">{{ priceError }}</p>
      <p v-else-if="!board?.hasData" class="market-status" role="status">暂无此物品在此范围的市场数据。</p>
      <template v-else>
        <div class="market-details-controls">
          <div class="segmented-control" role="group" aria-label="价格类型">
            <button type="button" :aria-pressed="tab === 'listings'" @click="tab = 'listings'">在售</button>
            <button type="button" :aria-pressed="tab === 'recentHistory'" @click="tab = 'recentHistory'">最近成交</button>
          </div>
          <small>数据更新于 {{ showTime(board.lastUploadTime) }}</small>
        </div>
        <div v-if="prices.length" class="market-price-header">
          <span>{{ tab === 'listings' ? '挂单' : '成交记录' }}</span><span>单价 · 金币</span>
        </div>
        <ul v-if="prices.length" class="market-rows market-prices">
          <li v-for="(row, index) in prices" :key="index" class="market-price">
            <div class="market-price-meta">
              <span><span v-if="row.hq" class="market-hq">HQ</span></span>
              <span class="market-quantity">数量 <span>{{ gil(row.quantity) }}</span></span>
              <div v-if="(dataCenterQuery && row.worldName) || row.timestamp" class="market-price-context">
                <small v-if="dataCenterQuery && row.worldName">{{ row.worldName }}</small>
                <small v-if="row.timestamp">{{ showTime(row.timestamp * 1000) }}</small>
              </div>
            </div>
            <strong>{{ gil(row.pricePerUnit) }}</strong>
          </li>
        </ul>
        <p v-else class="market-status" role="status">{{ tab === 'listings' ? '暂无在售挂单。' : '暂无成交记录。' }}</p>
      </template>
    </section>

    <section v-else-if="searched" class="paper market-card" aria-labelledby="market-search-results">
      <div class="market-title">
        <h2 id="market-search-results">搜索结果</h2>
        <span v-if="!searching && matches.length">{{ matches.length === 30 ? '前 30 项' : matches.length + ' 项' }}</span>
      </div>
      <p v-if="searching" class="market-status" role="status">正在搜索…</p>
      <div v-else-if="searchError" class="market-status" role="status">
        {{ searchError }} <button type="button" class="secondary" @click="query = searched; search()">重试</button>
      </div>
      <p v-else-if="!matches.length" class="market-status" role="status">未找到匹配的可交易道具。</p>
      <ul v-else class="market-rows">
        <li v-for="item in matches" :key="item.id">
          <button type="button" class="market-item" @click="openItem(item)">
            <ItemIcon :icon="item.icon" />
            <span class="market-item-text"><strong>{{ item.name }}</strong><small>#{{ gil(item.id) }}</small></span>
            <UiIcon name="right" :size="18" />
          </button>
        </li>
      </ul>
    </section>

    <section v-else class="paper market-card" aria-labelledby="market-recent-title">
      <div class="market-title">
        <span class="market-section-icon"><UiIcon name="chart" :size="20" /></span>
        <h2 id="market-recent-title">最近更新</h2>
        <button type="button" class="secondary" :disabled="recentLoading" @click="loadRecent">刷新</button>
      </div>
      <p v-if="recentLoading" class="market-status" role="status">正在读取 {{ scope }} 的市场数据…</p>
      <p v-else-if="recentError" class="market-status" role="status">{{ recentError }}</p>
      <p v-else-if="!recent?.length" class="market-status" role="status">暂无市场更新。</p>
      <ul v-else class="market-rows">
        <li v-for="entry in recent" :key="entry.item.id">
          <button type="button" class="market-item" @click="openItem(entry.item)">
            <ItemIcon :icon="entry.item.icon" />
            <span class="market-item-text"><strong>{{ entry.item.name }}</strong><small>#{{ gil(entry.item.id) }}</small></span>
            <span class="market-update"><small>{{ showTime(entry.lastUploadTime) }}</small>
              <small v-if="dataCenterQuery && entry.world">{{ entry.world }}</small></span>
            <UiIcon name="right" :size="18" />
          </button>
        </li>
      </ul>
    </section>
    <p class="market-source">市场数据由 Universalis 社区上传，价格以游戏内市场布告板为准。</p>
  </main>
</template>

<style scoped>
.market-search {
  display: flex;
  align-items: center;
  gap: 9px;
  min-width: 0;
  min-height: 58px;
  padding: 6px 7px 6px 17px;
  border-radius: 999px;
  color: var(--muted);
}
.market-search > svg { flex: none; }
.market-search:focus-within { border-color: var(--blue-border); }
.market-search input {
  flex: 1;
  min-width: 0;
  padding: 9px 2px;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--ink);
}
.market-submit, .market-clear {
  display: grid;
  place-items: center;
  flex: none;
  min-width: 40px;
  min-height: 40px;
  border: 0;
  border-radius: 50%;
  background: transparent;
}
.market-submit { width: 44px; height: 44px; background: var(--blue); color: white; }
.market-submit:disabled { background: var(--surface); color: #6e7786; }
.market-scopes { display: flex; flex-wrap: wrap; align-items: end; gap: 9px 12px; margin: 14px 0 20px; }
.market-scopes label { display: grid; gap: 5px; flex: 0 1 230px; min-width: 0; color: var(--muted); font-size: .84rem; font-weight: 650; }
.market-scopes select {
  min-width: 0;
  width: 100%;
  min-height: 40px;
  padding: 6px 10px;
  border: 1px solid var(--line);
  border-radius: var(--control-radius);
  background: var(--paper);
  color: var(--navy);
  font-size: .9rem;
}
.market-scopes .secondary { min-height: 40px; }
.market-location-error { display: flex; align-items: center; gap: 9px; margin: -8px 0 14px; color: var(--muted); font-size: .85rem; }
.market-card { min-width: 0; padding: 18px; }
.market-title { display: flex; align-items: center; gap: 10px; min-width: 0; margin-bottom: 14px; }
.market-title h2 { color: var(--navy); font-size: 1.18rem; font-weight: 780; overflow-wrap: anywhere; }
.market-title > .secondary, .market-title > span:last-child:not(.market-section-icon) { margin-left: auto; }
.market-title .secondary { flex: none; }
.market-section-icon {
  display: grid;
  place-items: center;
  flex: none;
  width: 33px;
  height: 33px;
  border-radius: 10px;
  color: var(--blue);
  background: var(--blue-light);
}
.market-title > span:last-child, .market-detail-title p, .market-price-header, .market-source, .market-status {
  color: var(--muted);
  font-size: .85rem;
}
.market-detail-title > div { flex: 1; min-width: 0; }
.market-detail-title > :first-child { flex: none; }
.market-detail-title p { margin-top: 3px; }
.market-status { padding: 16px 4px; line-height: 1.5; }
.market-back {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-height: 36px;
  margin: -4px 0 9px;
  padding: 4px 4px 4px 0;
  border: 0;
  background: transparent;
  color: var(--blue);
  font-size: .85rem;
  font-weight: 650;
}
.market-rows { display: grid; gap: 7px; margin: 0; padding: 0; list-style: none; }
.market-item {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 66px;
  padding: 8px 10px;
  text-align: left;
  border: 1px solid transparent;
  border-radius: 15px;
  background: var(--surface);
}
.market-item:hover { border-color: var(--blue-border); background: var(--blue-light); }
.market-item > :first-child, .market-item > svg { flex: none; }
.market-item > svg { color: var(--blue); }
.market-item-text { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.market-item strong, .market-item small { display: block; }
.market-item strong { line-height: 1.3; }
.market-item small, .market-details-controls small { margin-top: 3px; color: var(--muted); font-size: .76rem; }
.market-update { flex: none; text-align: right; }
.market-details-controls { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 7px 12px; }
.market-price-header { display: flex; justify-content: space-between; padding: 12px 9px 6px; }
.market-price {
  display: grid;
  grid-template-columns: minmax(0, 1fr) max-content;
  align-items: center;
  gap: 9px;
  padding: 9px 11px;
  background: var(--surface);
  border-radius: 11px;
  font-size: .9rem;
}
.market-price-meta {
  display: grid;
  grid-template-columns: 2.15em calc(2.35em + 3ch) minmax(0, 1fr);
  align-items: center;
  gap: 8px;
  font-variant-numeric: tabular-nums;
}
.market-quantity { display: flex; align-items: baseline; justify-content: space-between; white-space: nowrap; }
.market-price-context { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 8px; }
.market-price-context small { min-width: 0; color: var(--muted); font-size: .76rem; overflow-wrap: anywhere; }
.market-price strong { color: var(--blue); font-variant-numeric: tabular-nums; }
.market-hq { display: inline-block; padding: 1px 5px; border-radius: 4px; background: var(--amber-light); color: var(--amber); font-size: .72rem; font-weight: 730; }
.market-source { margin: 13px 4px 0; font-size: .75rem; line-height: 1.5; }
@container app-content (max-width: 560px) {
  .market-card { padding: 14px; }
  .market-search { gap: 6px; padding-left: 13px; }
  .market-item { gap: 8px; padding-inline: 7px; }
  .market-scopes { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
  .market-scopes .secondary { grid-column: 1 / -1; justify-self: start; }
}
</style>
