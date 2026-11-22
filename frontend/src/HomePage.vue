<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import ActivityCalendar from './ActivityCalendar.vue'
import { getOfficialBanners, getOfficialNews, type OfficialBanner, type OfficialNews } from './bridge'
import { chinaDay, eorzeaTime } from './home-time'
import UiIcon from './UiIcon.vue'
import './home.css'

const fallbackImages = [
  'home-fallback-1.jpg',
  'home-fallback-2.jpg',
  'home-fallback-3.jpg',
  'home-fallback-4.jpg',
  'home-fallback-5.jpg',
  'home-fallback-6.jpg',
]
const fallbackImage = `${import.meta.env.BASE_URL}images/${fallbackImages[Math.floor(Math.random() * fallbackImages.length)]}`
const now = ref(Date.now())
const clock = computed(() => eorzeaTime(now.value))
const today = computed(() => chinaDay(now.value))
const banners = ref<OfficialBanner[]>([])
const news = ref<OfficialNews[]>([])
const bannerLoading = ref(true)
const bannerFailed = ref(false)
const newsLoading = ref(true)
const newsFailed = ref(false)
const bannerIndex = ref(0)
const missingImages = ref(new Set<number>())
const selectedFilter = ref<'all' | 'announcement' | 'news'>('all')
const showMore = ref(false)
const filters = [
  { id: 'all', title: '全部' },
  { id: 'announcement', title: '公告' },
  { id: 'news', title: '资讯' },
] as const
const banner = computed(() => banners.value[bannerIndex.value])
const bannerImage = computed(() => banner.value?.image && !missingImages.value.has(banner.value.id)
  ? banner.value.image : fallbackImage)
const matchingNews = computed(() => news.value.filter(item => selectedFilter.value === 'all'
  || item.announcement === (selectedFilter.value === 'announcement')))
let ticker: ReturnType<typeof setInterval> | undefined

async function loadBanners() {
  bannerLoading.value = true
  bannerFailed.value = false
  try {
    const result = await getOfficialBanners()
    banners.value = result.items
    bannerFailed.value = result.failed
    bannerIndex.value = 0
    missingImages.value.clear()
  } catch {
    bannerFailed.value = true
  } finally {
    bannerLoading.value = false
  }
}

async function loadNews() {
  newsLoading.value = true
  newsFailed.value = false
  try {
    const result = await getOfficialNews()
    news.value = result.items.map(item => ({ ...item, published: normalizeDate(item.published) }))
    newsFailed.value = result.failed
  } catch {
    newsFailed.value = true
  } finally {
    newsLoading.value = false
  }
}

function moveBanner(offset: number) {
  bannerIndex.value = (bannerIndex.value + offset + banners.value.length) % banners.value.length
}

function imageFailed(event: Event, id: number | undefined) {
  if (id !== undefined && (event.currentTarget as HTMLImageElement).getAttribute('src') !== fallbackImage)
    missingImages.value.add(id)
}

function normalizeDate(published: string | null) {
  const parts = published?.match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})/)
  return parts ? `${parts[1]}-${parts[2].padStart(2, '0')}-${parts[3].padStart(2, '0')}` : null
}

onMounted(() => {
  void loadBanners()
  void loadNews()
  ticker = setInterval(() => { now.value = Date.now() }, 1000)
})
onBeforeUnmount(() => clearInterval(ticker))
</script>

<template>
  <main class="home-page" id="home">
    <div class="home-intro">
      <h1>今日艾欧泽亚</h1>
      <div class="eorzea-clock" :class="{ 'is-night': !clock.daylight }">
        <span class="clock-symbol"><UiIcon :name="clock.daylight ? 'sun' : 'moon'" :size="23" /></span>
        <div class="clock-copy">
          <span>艾欧泽亚时间</span>
          <strong>{{ clock.text }} <small>ET</small></strong>
        </div>
      </div>
    </div>

    <section class="home-banner" aria-label="官方推荐" aria-roledescription="轮播" @keydown.left.prevent="banners.length > 1 && moveBanner(-1)"
      @keydown.right.prevent="banners.length > 1 && moveBanner(1)">
      <img :key="bannerImage" class="banner-image" :src="bannerImage" alt="" referrerpolicy="no-referrer"
        @error="imageFailed($event, banner?.id)">
      <div class="banner-shade"></div>
      <div class="banner-content">
        <span v-if="banner" class="banner-label">官方推荐</span>
        <h2 v-if="banner"><a :href="banner.url" target="_blank" rel="noopener noreferrer">{{ banner.title }}</a></h2>
        <template v-else>
          <h2>官方推荐</h2>
          <p role="status">{{ bannerLoading ? '正在加载推荐…' : bannerFailed ? '暂时无法读取官方推荐。' : '暂无官方推荐。' }}</p>
          <button v-if="bannerFailed" class="banner-retry" type="button" @click="loadBanners">重试</button>
        </template>
      </div>
      <template v-if="banners.length > 1">
        <div class="banner-controls">
          <button type="button" aria-label="上一条推荐" @click="moveBanner(-1)"><UiIcon name="left" :size="18" /></button>
          <button type="button" aria-label="下一条推荐" @click="moveBanner(1)"><UiIcon name="right" :size="18" /></button>
        </div>
        <div class="banner-dots" aria-label="选择推荐">
          <button v-for="(item, index) in banners" :key="item.id" type="button"
            :aria-label="`第 ${index + 1} 条推荐`" :aria-pressed="index === bannerIndex" @click="bannerIndex = index"></button>
        </div>
      </template>
    </section>

    <div class="home-grid">
      <ActivityCalendar :now="now" />
      <div class="home-sidebar">
        <section class="home-card news-card" aria-labelledby="official-news">
          <div class="home-card-heading">
            <span class="home-card-icon"><UiIcon name="news" :size="19" /></span>
            <h2 id="official-news">官方信息</h2>
          </div>
          <div class="news-filters" aria-label="筛选官方信息">
            <button v-for="item in filters" :key="item.id" type="button" :aria-pressed="selectedFilter === item.id"
              @click="selectedFilter = item.id; showMore = false">{{ item.title }}</button>
          </div>
          <div v-if="newsLoading || newsFailed || !matchingNews.length" class="home-feed-message" role="status">
            <p v-if="newsLoading">正在加载官方信息…</p>
            <template v-else-if="newsFailed">
              <p>暂时无法读取官方信息。</p>
              <button class="home-text-button" type="button" @click="loadNews">重试</button>
            </template>
            <p v-else>暂无相关信息。</p>
          </div>
          <template v-else>
            <ul class="news-list">
              <li v-for="item in (showMore ? matchingNews : matchingNews.slice(0, 4))" :key="item.id">
                <a :href="item.url" target="_blank" rel="noopener noreferrer">
                  <span class="news-tag" :class="{ announcement: item.announcement }">{{ item.announcement ? '公告' : '资讯' }}</span>
                  <strong>{{ item.title }}</strong>
                  <time v-if="item.published" :datetime="item.published">{{ item.published.slice(5).replace('-', '.') }}</time>
                  <UiIcon class="news-link" name="external" :size="14" />
                </a>
              </li>
            </ul>
            <button v-if="matchingNews.length > 4" type="button" class="home-text-button more-news"
              :aria-expanded="showMore" @click="showMore = !showMore">{{ showMore ? '收起' : '更多官方信息' }}<UiIcon name="right" :size="15" /></button>
          </template>
        </section>
        <slot name="character" />
      </div>
    </div>
  </main>
</template>
