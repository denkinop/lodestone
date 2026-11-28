<script setup lang="ts">
import { computed, nextTick, ref, shallowRef } from 'vue'
import DatabaseMap from './DatabaseMap.vue'
import ItemIcon from './ItemIcon.vue'
import PageHeading from './PageHeading.vue'
import UiIcon from './UiIcon.vue'
import { getGameDatabaseEntry, searchGameDatabase, type DatabaseDetail, type DatabaseEntry } from './bridge'

const categories = [
  { type: '', label: '全部', icon: 'search' },
  { type: 'quest', label: '任务', icon: 'book' },
  { type: 'item', label: '物品', icon: 'cube' },
  { type: 'achievement', label: '成就', icon: 'check' },
  { type: 'instance', label: '副本', icon: 'location' },
  { type: 'action', label: '技能', icon: 'crystal' },
  { type: 'status', label: '状态与 Buff', icon: 'crystal' },
  { type: 'fate', label: 'FATE', icon: 'crystal' },
  { type: 'leve', label: '理符', icon: 'news' },
  { type: 'node', label: '采集点', icon: 'location' },
  { type: 'fishing', label: '钓鱼点', icon: 'location' },
  { type: 'npc', label: 'NPC', icon: 'user' },
  { type: 'mob', label: '怪物', icon: 'crystal' },
  { type: 'patch', label: '版本', icon: 'calendar' },
] as const

const pageSize = 24
const category = ref('')
const query = ref('')
const input = ref<HTMLInputElement>()
const detailHeading = ref<HTMLElement>()
const resultHeading = ref<HTMLElement>()
const results = shallowRef<DatabaseEntry[]>()
const searchError = ref('')
const searchNotice = ref('')
const searching = ref(false)
const submitted = ref('')
const page = ref(0)
const history = shallowRef<DatabaseEntry[]>([])
const detail = shallowRef<DatabaseDetail>()
const detailError = ref('')
const loadingDetail = ref(false)
let searchRequest = 0
let detailRequest = 0

const selected = computed(() => history.value.at(-1))
const isItem = computed(() => selected.value?.type === 'item')
const placeholder = computed(() => category.value === 'patch' ? '搜索版本号或名称'
  : category.value ? '搜索名称或 ID' : '搜索任务、物品、成就等')
const visibleResults = computed(() => results.value?.slice(page.value * pageSize, (page.value + 1) * pageSize))
const pageCount = computed(() => Math.ceil((results.value?.length ?? 0) / pageSize))
const typeInfo = (type: string) => categories.find(entry => entry.type === type) ?? categories[0]
const entryKey = (entry: DatabaseEntry) => entry.type + '/' + entry.id
const garlandUrl = (entry: DatabaseEntry) => 'https://www.garlandtools.cn/db/#'
  + encodeURIComponent(entry.type) + '/' + encodeURIComponent(entry.id)

async function search(term = query.value.trim()) {
  const request = ++searchRequest
  detailRequest++
  history.value = []
  results.value = undefined
  page.value = 0
  searchError.value = ''
  searchNotice.value = ''
  submitted.value = term
  searching.value = Boolean(term || category.value === 'patch')
  if (!searching.value) return
  try {
    const response = await searchGameDatabase(term, category.value)
    if (request !== searchRequest) return
    results.value = response.data ?? undefined
    searchError.value = response.error ?? ''
    searchNotice.value = response.notice ?? ''
  } catch {
    if (request === searchRequest) searchError.value = '无法搜索数据库，请重试。'
  } finally {
    if (request === searchRequest) searching.value = false
  }
}

function chooseCategory(type: string) {
  if (category.value === type) return
  category.value = type
  void search()
}

function clearSearch() {
  query.value = ''
  void search('')
  input.value?.focus()
}

async function loadDetail(entry: DatabaseEntry) {
  const request = ++detailRequest
  loadingDetail.value = true
  detail.value = undefined
  detailError.value = ''
  try {
    const response = await getGameDatabaseEntry(entry.type, entry.id)
    if (request !== detailRequest) return
    detail.value = response.data ?? undefined
    detailError.value = response.error ?? ''
  } catch {
    if (request === detailRequest) detailError.value = '无法读取条目，请重试。'
  } finally {
    if (request === detailRequest) loadingDetail.value = false
  }
}

function openEntry(entry: DatabaseEntry) {
  history.value = [...history.value, entry]
  void loadDetail(entry)
  void nextTick(() => detailHeading.value?.focus())
}

function back() {
  history.value = history.value.slice(0, -1)
  if (selected.value) {
    void loadDetail(selected.value)
    void nextTick(() => detailHeading.value?.focus())
  } else {
    detailRequest++
    void nextTick(() => resultHeading.value?.focus())
  }
}

function changePage(next: number) {
  page.value = next
  resultHeading.value?.focus()
}
</script>

<template>
  <main class="database-page">
    <PageHeading title="数据库" />

    <section class="paper database-controls" aria-label="数据库搜索">
      <form class="database-search" @submit.prevent="search()">
        <UiIcon name="search" :size="21" />
        <input ref="input" v-model="query" type="text" aria-label="数据库关键词"
          :placeholder="placeholder" enterkeyhint="search" autocomplete="off" maxlength="100">
        <button v-if="query" type="button" class="database-clear" aria-label="清除关键词" @click="clearSearch">
          <UiIcon name="close" :size="16" />
        </button>
        <button type="submit" class="primary" :disabled="!query.trim() && category !== 'patch'">搜索</button>
      </form>
      <div class="database-categories" role="group" aria-label="条目分类">
        <button v-for="entry in categories" :key="entry.type" type="button"
          :aria-pressed="category === entry.type" @click="chooseCategory(entry.type)">{{ entry.label }}</button>
      </div>
    </section>

    <section v-if="selected" class="paper database-panel" :class="{ 'database-item-detail': isItem }" aria-labelledby="database-detail-title">
      <button type="button" class="database-back" @click="back">
        <UiIcon name="left" :size="16" />{{ history.length > 1 ? '返回上一条' : '返回结果' }}
      </button>
      <div :class="{ 'database-item-overview': isItem }">
        <div class="database-detail-heading">
          <ItemIcon :icon="detail?.entry.icon || selected.icon" :fallback="typeInfo(selected.type).icon" />
          <div>
            <h2 id="database-detail-title" ref="detailHeading" tabindex="-1">{{ detail?.entry.name || selected.name }}</h2>
            <p>{{ typeInfo(selected.type).label }} · {{ selected.type === 'patch' ? selected.id : '#' + selected.id }}</p>
          </div>
          <a class="database-source" :href="garlandUrl(selected)" target="_blank" rel="noopener noreferrer">
            查看原页 <UiIcon name="external" :size="15" />
          </a>
        </div>
        <div v-if="detail && (detail.description || detail.fields.length)" :class="{ 'database-item-information': isItem }">
          <div v-if="detail.description" :class="{ 'database-item-description': isItem }">
            <h3 v-if="isItem">物品说明</h3>
            <p class="database-description">{{ detail.description }}</p>
          </div>
          <dl v-if="detail.fields.length" class="database-fields">
            <div v-for="field in detail.fields" :key="field.label">
              <dt>{{ field.label }}</dt><dd>{{ field.value }}</dd>
            </div>
          </dl>
        </div>
      </div>
      <p v-if="loadingDetail" class="database-status" role="status">正在读取条目…</p>
      <div v-else-if="detailError" class="database-status" role="status">
        <span>{{ detailError }}</span><button class="secondary" type="button" @click="loadDetail(selected)">重试</button>
      </div>
      <p v-else-if="!detail" class="database-status" role="status">Garland Tools 暂无此条目的详情。</p>
      <template v-else>
        <DatabaseMap v-if="detail.map" :key="entryKey(selected)" :location="detail.map" />
        <section v-for="group in detail.links" :key="group.label" class="database-related"
          :class="{ 'database-materials': isItem && group.label === '制作材料' }">
          <h3>{{ group.label }}</h3>
          <ul class="database-entries">
            <li v-for="entry in group.entries" :key="entryKey(entry)">
              <button type="button" class="database-entry" @click="openEntry(entry)">
                <ItemIcon :icon="entry.icon" :fallback="typeInfo(entry.type).icon" />
                <span>
                  <strong>{{ entry.name }}</strong>
                  <small v-if="!isItem || group.label !== '制作材料'">{{ entry.summary || typeInfo(entry.type).label }}</small>
                </span>
                <small v-if="isItem && group.label === '制作材料' && entry.summary" class="database-material-quantity">{{ entry.summary }}</small>
                <UiIcon name="right" :size="17" />
              </button>
            </li>
          </ul>
        </section>
        <p v-if="!detail.description && !detail.fields.length && !detail.links.length && !detail.map"
          class="database-status">暂无更多条目信息。</p>
      </template>
    </section>

    <section v-else class="paper database-panel" aria-labelledby="database-results-title">
      <div class="database-results-heading">
        <h2 id="database-results-title" ref="resultHeading" tabindex="-1">{{ category === 'patch' && !submitted ? '版本' : '查询结果' }}</h2>
        <span v-if="results?.length">{{ results.length }} 项</span>
      </div>
      <div v-if="searchNotice" class="database-status" role="status">
        <span>{{ searchNotice }}</span><button class="secondary" type="button" @click="search(submitted)">重试</button>
      </div>
      <p v-if="searching" class="database-status" role="status">正在查询 Garland Tools…</p>
      <div v-else-if="searchError" class="database-status" role="status">
        <span>{{ searchError }}</span><button class="secondary" type="button" @click="search(submitted)">重试</button>
      </div>
      <p v-else-if="!results" class="database-status">输入名称开始查询。选择分类后也可以按 ID 查找。</p>
      <p v-else-if="!results.length" class="database-status" role="status">未找到匹配的条目。</p>
      <ul v-else class="database-entries">
        <li v-for="entry in visibleResults" :key="entryKey(entry)">
          <button type="button" class="database-entry" @click="openEntry(entry)">
            <ItemIcon :icon="entry.icon" :fallback="typeInfo(entry.type).icon" />
            <span>
              <strong>{{ entry.name }}</strong>
              <small>{{ typeInfo(entry.type).label }} · {{ entry.type === 'patch' ? entry.id : '#' + entry.id }}<template v-if="entry.summary"> · {{ entry.summary }}</template></small>
            </span>
            <UiIcon name="right" :size="17" />
          </button>
        </li>
      </ul>
      <div v-if="pageCount > 1" class="database-pagination" aria-label="结果分页">
        <button type="button" class="secondary" :disabled="page === 0" @click="changePage(page - 1)">上一页</button>
        <span>{{ page + 1 }} / {{ pageCount }}</span>
        <button type="button" class="secondary" :disabled="page + 1 >= pageCount" @click="changePage(page + 1)">下一页</button>
      </div>
    </section>
  </main>
</template>

<style scoped>
.database-source, .database-back {
  display: inline-flex; align-items: center; gap: 6px; min-height: 36px;
  color: var(--blue); font-size: .85rem; font-weight: 650; text-decoration: none;
}
.database-source:hover { text-decoration: underline; }
.database-controls, .database-panel { min-width: 0; padding: 18px; }
.database-controls { margin-bottom: 16px; }
.database-search {
  display: flex; align-items: center; gap: 8px; padding: 5px 6px 5px 12px;
  border: 1px solid var(--line); border-radius: var(--control-radius); background: var(--surface); color: var(--muted);
}
.database-search:focus-within { border-color: var(--blue-border); }
.database-search > svg, .database-search > button { flex: none; }
.database-search input { flex: 1; min-width: 0; min-height: 36px; padding: 4px; border: 0; background: transparent; color: var(--ink); }
.database-search input:focus-visible { outline: 0; }
.database-clear {
  display: grid; place-items: center; width: 33px; height: 36px; padding: 0;
  border: 0; border-radius: var(--control-radius); background: transparent;
}
.database-categories { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 12px; }
.database-categories button {
  min-height: 36px; padding: 5px 12px; border: 1px solid transparent; border-radius: 999px;
  background: var(--surface); color: var(--muted); font-size: .84rem; font-weight: 650; white-space: nowrap;
}
.database-categories button:hover, .database-categories button[aria-pressed="true"] { border-color: var(--blue-border); }
.database-categories button[aria-pressed="true"] { color: var(--blue); background: var(--blue-light); }
.database-results-heading, .database-detail-heading { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
.database-results-heading h2, .database-detail-heading h2 { color: var(--navy); font-size: 1.18rem; font-weight: 750; overflow-wrap: anywhere; }
.database-results-heading > span, .database-detail-heading p, .database-pagination span { font-size: .84rem; color: var(--muted); }
.database-results-heading > span, .database-detail-heading > a { margin-inline-start: auto; }
.database-detail-heading > .item-icon, .database-detail-heading > a { flex: none; }
.database-detail-heading p { margin-top: 4px; }
.database-back { margin: -3px 0 9px; padding: 0; border: 0; background: transparent; }
.database-status { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 12px 0; font-size: .9rem; color: var(--muted); }
.database-entries {
  display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px;
  list-style: none; margin: 0; padding: 0;
}
.database-entry {
  display: flex; align-items: center; gap: 10px; width: 100%; min-height: 58px; padding: 8px;
  border: 1px solid transparent; border-radius: var(--control-radius); background: var(--surface); text-align: left;
}
.database-entry:hover { background: var(--blue-light); border-color: var(--blue-border); }
.database-entry > .item-icon { width: 40px; height: 40px; flex: none; }
.database-entry > span:nth-child(2) { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.database-entry > svg { flex: none; color: var(--blue); }
.database-entry strong, .database-entry small { display: block; }
.database-entry small { margin-top: 3px; color: var(--muted); font-size: .78rem; }
.database-pagination { display: flex; align-items: center; justify-content: center; gap: 14px; margin-top: 15px; }
.database-description { margin: 14px 0; white-space: pre-line; overflow-wrap: anywhere; line-height: 1.65; }
.database-fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(9rem, 100%), 1fr)); gap: 8px; }
.database-fields div { padding: 9px 11px; border-radius: var(--control-radius); background: var(--surface); overflow-wrap: anywhere; }
.database-fields dt { color: var(--muted); font-size: .78rem; }
.database-fields dd { margin-top: 4px; font-size: .9rem; font-weight: 650; }
.database-related { margin-top: 17px; }
.database-related h3 { margin-bottom: 8px; color: var(--navy); font-size: .95rem; }
.database-item-overview {
  padding: clamp(14px, 2.4cqi, 23px);
  border: 1px solid var(--line);
  border-radius: 17px;
  background: linear-gradient(130deg, var(--blue-light), var(--surface) 70%);
}
.database-item-overview .database-detail-heading { margin-bottom: 0; }
.database-item-overview .database-detail-heading > div { min-width: 0; }
.database-item-overview .database-detail-heading > .item-icon {
  width: 64px; height: 64px; border: 1px solid var(--line); border-radius: 14px;
  background: var(--paper); box-shadow: var(--panel-shadow);
}
.database-item-overview .database-detail-heading h2 { font-size: clamp(1.2rem, 2.6cqi, 1.5rem); }
.database-item-overview .database-source {
  padding: 5px 10px; border: 1px solid var(--line); border-radius: var(--control-radius);
  background: var(--paper); white-space: nowrap;
}
.database-item-information {
  display: grid; gap: 12px; margin-top: 15px; padding-top: 15px; border-top: 1px solid var(--line);
}
.database-item-description {
  min-width: 0; padding: 12px 14px; border-inline-start: 3px solid var(--blue-border);
  border-radius: 10px; background: var(--paper);
}
.database-item-description h3 { color: var(--navy); font-size: .8rem; font-weight: 700; }
.database-item-description .database-description {
  max-inline-size: 46em; margin: 5px 0 0; color: var(--ink); font-size: .94rem; line-height: 1.75;
}
.database-item-information .database-fields {
  grid-template-columns: repeat(auto-fit, minmax(min(7.5rem, 100%), 1fr));
  align-content: start;
}
.database-item-information .database-fields > div { border: 1px solid var(--line); background: var(--paper); }
.database-item-information .database-fields dd { font-variant-numeric: tabular-nums; }
.database-item-detail .database-related { margin-top: 20px; }
.database-item-detail .database-related h3 { margin-bottom: 9px; }
.database-item-detail .database-related .database-entry { border-color: var(--line); background: var(--paper); }
.database-item-detail .database-related .database-entry:hover { border-color: var(--blue-border); background: var(--blue-light); }
.database-materials .database-material-quantity {
  flex: none; margin: 0; padding: 3px 7px; border-radius: 6px;
  background: var(--blue-light); color: var(--blue); font-variant-numeric: tabular-nums; white-space: nowrap;
}
@container app-content (min-width: 720px) {
  .database-item-information:has(.database-item-description):has(.database-fields) {
    grid-template-columns: minmax(0, 1.15fr) minmax(0, 1fr);
  }
}
@container app-content (max-width: 590px) {
  .database-controls, .database-panel { padding: 14px; }
  .database-entries { grid-template-columns: minmax(0, 1fr); }
  .database-categories button { padding-inline: 10px; }
  .database-detail-heading { flex-wrap: wrap; }
  .database-item-overview .database-detail-heading {
    display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 6px 10px;
  }
  .database-item-overview .database-detail-heading > .item-icon {
    grid-row: 1 / 3; align-self: start; width: 52px; height: 52px;
  }
  .database-item-overview .database-source {
    grid-column: 2; justify-self: start; min-height: 29px; margin: 0; padding: 3px 8px;
  }
}
</style>
