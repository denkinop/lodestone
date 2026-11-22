<script setup lang="ts">
import { computed, onActivated, onDeactivated, ref, watch } from 'vue'
import PageHeading from './PageHeading.vue'
import UiIcon from './UiIcon.vue'
import { chinaDay, eorzeaTime, twoDigits } from './home-time'
import { forecastPeriods, matchingWeather, weatherPeriodMs, type WeatherCatalog, type WeatherPeriod } from './weather'

const props = defineProps<{ catalog: WeatherCatalog }>()
const regions = computed(() => [...props.catalog.regions].sort((a, b) => a.name.localeCompare(b.name, 'zh-CN')))
const weather = computed(() => props.catalog.weather)
const initialRegion = computed(() => regions.value.find(region => region.maps.some(map => map.id === 146)) ?? regions.value[0])
const regionId = ref(initialRegion.value.id)
const mapId = ref(initialRegion.value.maps.find(map => map.id === 146)?.id ?? initialRegion.value.maps[0].id)
const region = computed(() => regions.value.find(item => item.id === regionId.value) ?? initialRegion.value)
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
const historyRows = 2
const precedingPeriods = computed(() => historyRows * 3 + Math.floor(now.value / weatherPeriodMs) % 3)
const periods = computed(() => forecastPeriods(map.value.rates, now.value, precedingPeriods.value))
const current = computed(() => periods.value[precedingPeriods.value])
const days = computed(() => {
  const rows: { key: number; date: string; cells: (WeatherPeriod | undefined)[] }[] = []
  for (const period of periods.value) {
    if (period.hour === 0) rows.push({ key: period.start, date: chinaDay(period.start), cells: [undefined, undefined, undefined] })
    rows[rows.length - 1].cells[period.hour / 8] = period
  }
  return rows
})
const matches = computed(() => matchingWeather(periods.value.slice(precedingPeriods.value), {
  weather: desired.value, previous: previous.value, hours: hours.value,
}, now.value))
const initialRows = 10
const rowLimit = ref(initialRows)
const canShowMore = computed(() => rowLimit.value < (filtered.value ? matches.value.length : days.value.length - historyRows))

watch([mapId, desired, previous, hours], () => { rowLimit.value = initialRows }, { deep: true })
watch(() => props.catalog, () => {
  regionId.value = region.value.id
  mapId.value = map.value.id
  const available = new Set(weatherOptions.value)
  for (const values of [desired.value, previous.value]) {
    for (const id of values) if (!available.has(id)) values.delete(id)
  }
})

const timeFormat = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai', hourCycle: 'h23', hour: '2-digit', minute: '2-digit', second: '2-digit',
})
const dateLabel = (time: number) => chinaDay(time).slice(5).replace('-', '/')
const timeLabel = (time: number) => timeFormat.format(time)
const dateTime = (time: number) => new Date(time).toISOString()
const iconPath = (id: number) => `data:image/webp;base64,${weather.value[id].icon}`
</script>

<template>
  <main class="weather-page">
    <PageHeading title="天气查询">
      <div class="weather-clock" aria-label="艾欧泽亚时间">
        <UiIcon :name="clock.daylight ? 'sun' : 'moon'" :size="19" />
        <span>ET <strong>{{ clock.text }}</strong></span>
      </div>
    </PageHeading>

    <section class="paper weather-controls" aria-label="地图和筛选条件">
      <div class="weather-location">
        <label>地区
          <select v-model="regionId" @change="selectRegion">
            <option v-for="item in regions" :key="item.id" :value="item.id">{{ item.name }}</option>
          </select>
        </label>
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
            <strong class="current-weather-value"><img :src="iconPath(current.weather)" alt="">{{ weather[current.weather].name }}</strong>
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
      <table v-else class="period-table" aria-label="地图天气预报，日期和现实时间均为北京时间">
        <thead>
          <tr>
            <th class="period-date" scope="col" title="北京时间">日期</th>
            <th v-for="hour in [0, 8, 16]" :key="hour" scope="col">ET {{ twoDigits(hour) }}:00</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="day in days.slice(0, rowLimit + historyRows)" :key="day.key">
            <th class="period-date" scope="row" :class="{ 'past-period': day.key + 2 * weatherPeriodMs < current.start }">
              <time :datetime="day.date">{{ dateLabel(day.key) }}</time>
            </th>
            <td v-for="(period, index) in day.cells" :key="index" :class="{
              'active-period': period?.start === current.start,
              'past-period': period && period.start < current.start,
            }" :aria-current="period?.start === current.start ? 'time' : undefined">
              <div v-if="period" class="period-entry">
                <span class="period-clock">
                  <time :datetime="dateTime(period.start)">{{ timeLabel(period.start) }}</time>
                  <abbr v-if="chinaDay(period.start) !== day.date" class="period-next-day" title="北京时间次日">+1</abbr>
                </span>
                <span class="weather-name"><img :src="iconPath(period.weather)" alt=""><span>{{ weather[period.weather].name }}</span></span>
              </div>
              <span v-else class="period-empty">—</span>
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
.weather-clock { display: flex; align-items: center; gap: 8px; padding: 8px 12px; color: var(--weather-accent); background: var(--weather-tint); border: 1px solid #ccd5eb; border-radius: 999px; white-space: nowrap; font-size: .85rem; }
.weather-clock strong, time, .match-et, .hour-grid { font-variant-numeric: tabular-nums; }
.weather-location { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
.weather-location label { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-width: 0; padding: 12px 20px; font-weight: 700; font-size: .9rem; }
.weather-location label + label { border-inline-start: 1px solid var(--line); }
.weather-location select { width: min(72%, 15rem); min-height: 40px; padding: 5px 10px; border: 1px solid var(--line); border-radius: var(--control-radius); background: var(--surface); font-weight: 550; }
.weather-filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 15px 20px; padding: 17px 20px 20px; border-top: 1px solid var(--line); }
.weather-filters fieldset { min-width: 0; margin: 0; padding: 0; border: 0; }
.weather-filters legend { padding: 0; margin-bottom: 7px; color: var(--navy); font-size: .875rem; font-weight: 700; }
.filter-buttons { display: flex; flex-wrap: wrap; gap: 5px; }
.filter-buttons button { display: inline-flex; align-items: center; justify-content: center; gap: 5px; min-height: 34px; padding: 3px 8px; border: 1px solid transparent; border-radius: var(--control-radius); background: var(--surface); font-size: .8125rem; white-space: nowrap; }
.filter-buttons button:hover { border-color: var(--weather-accent); background: var(--surface); }
.filter-buttons button[aria-pressed="true"] { border-color: var(--weather-accent); background: var(--weather-tint); color: var(--weather-accent); font-weight: 700; }
.filter-buttons img, .current-weather img { width: 18px; height: 18px; }
.hour-filter { grid-column: 1 / -1; }
.hour-options { display: flex; flex-wrap: wrap; gap: 7px; align-items: start; }
.hour-grid { display: grid; grid-template-columns: repeat(24, minmax(0, 1fr)); flex: 1; gap: 4px; }
.hour-grid button { min-width: 0; padding-inline: 0; }
.forecast-panel { margin-top: 17px; overflow: hidden; }
.forecast-heading { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 17px 22px 13px; background: linear-gradient(110deg, #f1f3fb, var(--paper) 75%); }
.forecast-heading h2 { color: var(--navy); font-size: 1.22rem; font-weight: 750; }
.current-weather { display: flex; align-items: center; flex-wrap: wrap; gap: 5px; margin-top: 6px; color: var(--muted); font-size: .875rem; }
.current-weather-value { display: inline-flex; align-items: center; gap: 5px; padding: 3px 8px; border-radius: 999px; color: var(--weather-accent); background: var(--weather-tint); }
.forecast-heading .secondary { flex-shrink: 0; }
.match-status { padding: 0 22px 14px; color: var(--muted); font-size: .875rem; }
.forecast-panel table { width: 100%; border-collapse: collapse; table-layout: fixed; }
.forecast-panel thead th { padding: 8px; background: var(--surface); color: var(--muted); font-weight: 650; font-size: .8125rem; }
.forecast-panel td { padding: 7px 8px; border-top: 1px solid var(--line); }
.forecast-actions { display: flex; justify-content: center; padding: 12px; border-top: 1px solid var(--line); }
.period-table .period-date { padding-inline: 12px; text-align: left; }
.period-table thead .period-date { width: 12%; }
.period-table tbody .period-date { border-top: 1px solid var(--line); font-size: .83rem; font-weight: 500; }
.period-table td { padding-block: 7px; text-align: center; }
.period-entry { display: flex; justify-content: center; align-items: center; flex-wrap: wrap; gap: 2px 9px; }
.period-clock { display: inline-flex; align-items: center; gap: 3px; white-space: nowrap; font-size: .83rem; }
.period-next-day { color: var(--muted); font-size: .7em; text-decoration: none; }
.past-period, .period-empty { color: var(--muted); }
.past-period img { opacity: .62; }
.active-period { color: var(--weather-accent); background: var(--weather-tint); }
.weather-name { display: inline-flex; align-items: center; justify-content: center; gap: 5px; font-weight: 600; font-size: .875rem; }
.weather-name img { flex: none; width: 22px; height: 22px; }
.period-entry .weather-name img { width: 18px; height: 18px; }
.match-table td { text-align: center; }
.match-table th:first-child { width: 39%; }
.match-table th:nth-child(2) { width: 21%; }
.match-table time { white-space: nowrap; }
.match-time, .match-et { color: var(--muted); font-size: .8125rem; }
@container app-content (max-width: 940px) {
  .hour-grid { grid-template-columns: repeat(12, minmax(27px, 1fr)); }
}
@container app-content (max-width: 620px) {
  .weather-location { grid-template-columns: minmax(0, 1fr); }
  .weather-location label { padding: 9px 13px; }
  .weather-location label + label { border-inline-start: 0; border-top: 1px solid var(--line); }
  .forecast-heading { padding: 13px; }
  .weather-filters { grid-template-columns: minmax(0, 1fr); gap: 13px; padding: 14px 13px; }
  .hour-grid { grid-template-columns: repeat(8, minmax(26px, 1fr)); }
  .weather-name { font-size: .8125rem; }
  .period-table td, .period-table th, .period-table .period-date { padding-inline: 4px; }
  .period-table .period-date { padding-inline-start: 7px; }
  .period-table thead .period-date { width: 48px; }
  .period-entry { gap: 2px 5px; }
  .period-clock, .period-table tbody .period-date { font-size: .75rem; }
  .period-entry .weather-name { gap: 3px; }
  .period-entry .weather-name img { width: 16px; height: 16px; }
  .match-status { padding-inline: 13px; }
  .match-table th:first-child { width: 33%; }
  .match-table th:nth-child(2) { width: 21%; }
  .match-table td { padding-inline: 4px; }
  .match-time > span { display: none; }
  .match-time time { display: block; }
  .match-time, .match-et { font-size: .75rem; }
  .match-table .weather-name { display: grid; justify-items: center; gap: 2px; }
}
@container app-content (max-width: 470px) {
  .period-entry { flex-direction: column; }
}
@container app-content (max-width: 360px) {
  .hour-options { display: grid; }
  .period-clock { gap: 2px; font-size: .6875rem; }
}
</style>
