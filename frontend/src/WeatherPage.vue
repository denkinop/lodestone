<script setup lang="ts">
import { computed, onActivated, onDeactivated, ref, watch } from 'vue'
import UiIcon from './UiIcon.vue'
import { chinaDay, eorzeaTime, twoDigits } from './home-time'
import catalog from './weather-data.json'
import { forecastPeriods, matchingWeather, type WeatherPeriod } from './weather'

const regions = [...catalog.regions].sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'))
const weather: Record<number, { name: string; icon: string }> = catalog.weather
const initialRegion = regions.find(region => region.maps.some(map => map.id === 146)) ?? regions[0]
const regionId = ref(initialRegion.id)
const mapId = ref(initialRegion.maps.find(map => map.id === 146)?.id ?? initialRegion.maps[0].id)
const region = computed(() => regions.find(item => item.id === regionId.value) ?? initialRegion)
const maps = computed(() => [...region.value.maps].sort((a, b) => a.name.localeCompare(b.name, 'zh-CN')))
const map = computed(() => maps.value.find(item => item.id === mapId.value) ?? maps.value[0])
const weatherOptions = computed(() => [...new Set(map.value.rates.map(rate => rate.weather))])

const desired = ref(new Set<number>())
const previous = ref(new Set<number>())
const hours = ref(new Set<number>())
const weatherFilters = computed(() => [
  { label: '天气', values: desired.value }, { label: '前置天气', values: previous.value },
])
const filtered = computed(() => Boolean(desired.value.size || previous.value.size || hours.value.size))
const etHours = Array.from({ length: 24 }, (_, hour) => hour)

function toggle(values: Set<number>, value: number) {
  if (values.has(value)) values.delete(value)
  else values.add(value)
}

function selectRegion() {
  mapId.value = maps.value[0].id
  resetWeather()
}

function resetWeather() {
  desired.value.clear()
  previous.value.clear()
}

function resetFilters() {
  resetWeather()
  hours.value.clear()
}

const now = ref(Date.now())
let timer: ReturnType<typeof setInterval>
onActivated(() => {
  now.value = Date.now()
  timer = setInterval(() => { now.value = Date.now() }, 1000)
})
onDeactivated(() => clearInterval(timer))

const clock = computed(() => eorzeaTime(now.value))
const periods = computed(() => forecastPeriods(map.value.rates, now.value))
const current = computed(() => periods.value[0])
const days = computed(() => {
  const rows: { key: number; cells: (WeatherPeriod | undefined)[] }[] = []
  for (const period of periods.value) {
    if (!rows.length || period.hour === 0) rows.push({ key: period.start, cells: Array(3).fill(undefined) })
    rows[rows.length - 1].cells[period.hour / 8] = period
  }
  return rows
})
const matches = computed(() => matchingWeather(periods.value, {
  weather: desired.value, previous: previous.value, hours: hours.value,
}, now.value))
const initialRows = 10
const rowLimit = ref(initialRows)
const canShowMore = computed(() => rowLimit.value < (filtered.value ? matches.value.length : days.value.length))

watch([mapId, desired, previous, hours], () => { rowLimit.value = initialRows }, { deep: true })

const timeFormat = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai', hourCycle: 'h23', hour: '2-digit', minute: '2-digit', second: '2-digit',
})
const dateLabel = (time: number) => chinaDay(time).slice(5).replace('-', '/')
const timeLabel = (time: number) => timeFormat.format(time)
const dateTime = (time: number) => new Date(time).toISOString()
const iconPath = (id: number) => `${import.meta.env.BASE_URL}images/weather/${weather[id].icon}`
</script>

<template>
  <main class="narrow-page weather-page">
    <div class="page-heading">
      <div>
        <h1>天气查询</h1>
        <p>地图天气预报 · 现实时间为北京时间</p>
      </div>
      <div class="weather-clock" aria-label="艾欧泽亚时间">
        <UiIcon :name="clock.daylight ? 'sun' : 'moon'" :size="19" />
        <span>ET <strong>{{ clock.text }}</strong></span>
      </div>
    </div>

    <section class="paper weather-controls" aria-label="地图和筛选条件">
      <div class="weather-location">
        <label>地区
          <select v-model="regionId" @change="selectRegion">
            <option v-for="item in regions" :key="item.id" :value="item.id">{{ item.name }}</option>
          </select>
        </label>
        <UiIcon name="right" :size="18" />
        <label>地图
          <select v-model="mapId" @change="resetWeather">
            <option v-for="item in maps" :key="item.id" :value="item.id">{{ item.name }}</option>
          </select>
        </label>
      </div>

      <div class="weather-filters">
        <fieldset v-for="filter in weatherFilters" :key="filter.label">
          <legend>{{ filter.label }}</legend>
          <div class="filter-buttons">
            <button type="button" :aria-pressed="!filter.values.size" @click="filter.values.clear()">任意</button>
            <button v-for="id in weatherOptions" :key="id" type="button" :aria-pressed="filter.values.has(id)"
              @click="toggle(filter.values, id)">
              <img :src="iconPath(id)" alt="">{{ weather[id].name }}
            </button>
          </div>
        </fieldset>
        <fieldset class="hour-filter">
          <legend>艾欧泽亚时间 (ET)</legend>
          <div class="hour-options">
            <div class="filter-buttons">
              <button type="button" :aria-pressed="!hours.size" @click="hours.clear()">全天</button>
            </div>
            <div class="filter-buttons hour-grid">
              <button v-for="hour in etHours" :key="hour" type="button" :aria-pressed="hours.has(hour)"
                :aria-label="`ET ${twoDigits(hour)}:00 至 ${twoDigits((hour + 1) % 24)}:00`"
                @click="toggle(hours, hour)">{{ twoDigits(hour) }}</button>
            </div>
          </div>
        </fieldset>
      </div>
    </section>

    <section class="paper forecast-panel" aria-labelledby="weather-forecast-heading">
      <div class="forecast-heading">
        <div>
          <h2 id="weather-forecast-heading">未来 48 小时</h2>
          <p class="current-weather">
            {{ map.name }}当前天气
            <img :src="iconPath(current.weather)" alt=""><strong>{{ weather[current.weather].name }}</strong>
          </p>
        </div>
        <button v-if="filtered" class="secondary" type="button" @click="resetFilters">清空筛选</button>
      </div>

      <template v-if="filtered">
        <p class="match-status" role="status">{{ matches.length ? `符合条件的时段 · ${matches.length} 个` : '未来 48 小时内没有符合条件的时段。' }}</p>
        <table v-if="matches.length" class="match-table" aria-label="符合条件的天气时段">
          <thead><tr><th scope="col">北京时间</th><th scope="col">ET</th><th scope="col">天气</th><th scope="col">前置天气</th></tr></thead>
          <tbody>
            <tr v-for="match in matches.slice(0, rowLimit)" :key="match.key">
              <td class="match-time">
                <time :datetime="dateTime(match.start)">{{ dateLabel(match.start) }} {{ timeLabel(match.start) }}</time>
                <span aria-hidden="true"> – </span>
                <time :datetime="dateTime(match.end)"><template v-if="dateLabel(match.start) !== dateLabel(match.end)">{{ dateLabel(match.end) }} </template>{{ timeLabel(match.end) }}</time>
              </td>
              <td class="match-et">{{ eorzeaTime(match.start).text }}–{{ eorzeaTime(match.end).text }}</td>
              <td><span class="weather-name"><img :src="iconPath(match.period.weather)" alt="">{{ weather[match.period.weather].name }}</span></td>
              <td><span class="weather-name"><img :src="iconPath(match.period.previous)" alt="">{{ weather[match.period.previous].name }}</span></td>
            </tr>
          </tbody>
        </table>
      </template>
      <table v-else class="period-table" aria-label="地图天气预报">
        <thead><tr><th v-for="hour in [0, 8, 16]" :key="hour" scope="col">ET {{ twoDigits(hour) }}:00</th></tr></thead>
        <tbody>
          <tr v-for="day in days.slice(0, rowLimit)" :key="day.key">
            <td v-for="(period, index) in day.cells" :key="index" :class="{ 'active-period': period?.start === current.start }">
              <template v-if="period">
                <span class="weather-name"><img :src="iconPath(period.weather)" alt="">{{ weather[period.weather].name }}</span>
                <span class="period-time">
                  <time :datetime="dateTime(period.start)">{{ dateLabel(period.start) }} {{ timeLabel(period.start) }}</time>
                  <span v-if="period.start === current.start" class="current-label">当前</span>
                </span>
              </template>
              <span v-else class="period-time">—</span>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="canShowMore" class="forecast-actions">
        <button class="secondary" type="button" @click="rowLimit += 5">显示更多</button>
      </div>
    </section>
  </main>
</template>

<style scoped>
.weather-page { max-width: 1080px; }
.weather-clock { display: flex; align-items: center; gap: 8px; padding: 8px 12px; color: var(--blue-hover); background: var(--blue-light); border: 1px solid var(--line); border-radius: 12px; white-space: nowrap; font-size: .85rem; }
.weather-clock strong, time, .match-et, .hour-grid { font-variant-numeric: tabular-nums; }
.weather-controls { padding: 20px 22px; }
.weather-location { display: grid; grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr); align-items: end; gap: 10px; max-width: 540px; }
.weather-location label { display: grid; min-width: 0; gap: 7px; font-weight: 700; font-size: .9rem; }
.weather-location > svg { align-self: end; margin-bottom: 10px; color: var(--muted); }
.weather-location select { width: 100%; min-height: 39px; padding: 5px 10px; border: 1px solid var(--line); border-radius: 8px; background: var(--paper); font-weight: 500; }
.weather-filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 15px 20px; margin-top: 17px; padding-top: 15px; border-top: 1px solid var(--line); }
.weather-filters fieldset { min-width: 0; margin: 0; padding: 0; border: 0; }
.weather-filters legend { padding: 0; margin-bottom: 7px; color: var(--navy); font-size: .875rem; font-weight: 700; }
.filter-buttons { display: flex; flex-wrap: wrap; gap: 5px; }
.filter-buttons button { display: inline-flex; align-items: center; justify-content: center; gap: 5px; min-height: 32px; padding: 3px 8px; border: 1px solid var(--line); border-radius: 7px; background: var(--paper); font-size: .8125rem; white-space: nowrap; }
.filter-buttons button:hover { border-color: var(--blue); }
.filter-buttons button[aria-pressed="true"] { border-color: var(--blue); background: var(--blue-light); color: var(--blue-hover); font-weight: 700; }
.filter-buttons img, .current-weather img { width: 18px; height: 18px; }
.hour-filter { grid-column: 1 / -1; }
.hour-options { display: flex; flex-wrap: wrap; gap: 7px; align-items: start; }
.hour-grid { display: grid; grid-template-columns: repeat(24, minmax(0, 1fr)); flex: 1; gap: 4px; }
.hour-grid button { min-width: 0; padding-inline: 0; }
.forecast-panel { margin-top: 17px; overflow: hidden; }
.forecast-heading { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 17px 22px 13px; }
.forecast-heading h2 { color: var(--navy); font: 700 1.32rem var(--heading); }
.current-weather { display: flex; align-items: center; flex-wrap: wrap; gap: 5px; margin-top: 6px; color: var(--muted); font-size: .875rem; }
.current-weather strong { color: var(--ink); }
.forecast-heading .secondary { flex-shrink: 0; }
.match-status { padding: 0 22px 14px; color: var(--muted); font-size: .875rem; }
.forecast-panel table { width: 100%; border-collapse: collapse; table-layout: fixed; }
.forecast-panel th { padding: 8px; background: #f2f6fc; color: var(--muted); font-weight: 650; font-size: .8125rem; }
.forecast-panel td { padding: 7px 8px; border-top: 1px solid var(--line); }
.forecast-actions { display: flex; justify-content: center; padding: 12px; border-top: 1px solid var(--line); }
.period-table td { text-align: center; }
.period-table td + td { border-left: 1px solid var(--line); }
.active-period { background: var(--blue-light); }
.weather-name { display: inline-flex; align-items: center; justify-content: center; gap: 5px; font-weight: 600; font-size: .875rem; }
.weather-name img { flex: none; width: 22px; height: 22px; }
.period-time { display: flex; justify-content: center; align-items: center; flex-wrap: wrap; gap: 2px 6px; margin-top: 2px; color: var(--muted); font-size: .8125rem; }
.current-label { color: var(--blue-hover); font-size: .75rem; font-weight: 750; }
.match-table td { text-align: center; }
.match-table th:first-child { width: 39%; }
.match-table th:nth-child(2) { width: 21%; }
.match-table time { white-space: nowrap; }
.match-time, .match-et { color: var(--muted); font-size: .8125rem; }
@media (max-width: 940px) {
  .hour-grid { grid-template-columns: repeat(12, minmax(27px, 1fr)); }
}
@media (max-width: 620px) {
  .weather-controls, .forecast-heading { padding: 13px; }
  .weather-filters { grid-template-columns: minmax(0, 1fr); gap: 12px; }
  .hour-grid { grid-template-columns: repeat(8, minmax(26px, 1fr)); }
  .weather-name { font-size: .8125rem; }
  .match-status { padding-inline: 13px; }
  .match-table th:first-child { width: 33%; }
  .match-table th:nth-child(2) { width: 21%; }
  .match-table td { padding-inline: 4px; }
  .match-time > span { display: none; }
  .match-time time { display: block; }
  .match-time, .match-et { font-size: .75rem; }
  .match-table .weather-name { display: grid; justify-items: center; gap: 2px; }
}
@media (max-width: 360px) {
  .hour-options { display: grid; }
  .period-time { font-size: .75rem; }
}
</style>
