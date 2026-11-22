<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { getOfficialCalendar } from './bridge'
import { calendarDate, chinaDay, twoDigits } from './home-time'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ now: number }>()
const weekdays = ['一', '二', '三', '四', '五', '六', '日']
const colors = ['#4164a5', '#6e8dbd', '#4a81ad', '#5a728f']
const today = computed(() => chinaDay(props.now))
const currentMonth = computed(() => today.value.slice(0, 7))
const month = ref(currentMonth.value)
const selected = ref('')
const request = ref(0)
const loading = ref(true)
const failed = ref(false)
const activities = ref<{
  id: number; name: string; start: number; end: number; color: string; url: string | null; first: string; last: string
}[]>([])
const monthDate = computed(() => calendarDate(month.value))
const visible = computed(() => selected.value ? activities.value.filter(event => covers(event, selected.value)) : activities.value)

function covers(event: { first: string; last: string }, day: string) {
  return event.first <= day && day <= event.last
}

const days = computed(() => {
  const { year, month: number } = monthDate.value
  const first = (new Date(Date.UTC(year, number - 1, 1)).getUTCDay() + 6) % 7
  const length = new Date(Date.UTC(year, number, 0)).getUTCDate()
  return Array.from({ length: Math.max(35, Math.ceil((first + length) / 7) * 7) }, (_, index) => {
    const day = index - first + 1
    if (day < 1 || day > length) return null
    const key = `${month.value}-${twoDigits(day)}`
    return { day, key, events: activities.value.filter(event => covers(event, key)) }
  })
})

watch(currentMonth, (next, previous) => {
  if (month.value === previous && !selected.value) month.value = next
})

watch([month, request], async ([target], _, onCleanup) => {
  let valid = true
  onCleanup(() => { valid = false })
  loading.value = true
  failed.value = false
  activities.value = []
  try {
    const result = await getOfficialCalendar(target)
    if (!valid) return
    failed.value = result.failed
    activities.value = result.items.map((event, index) => ({
      ...event,
      color: event.color && /^#[\da-f]{6}$/i.test(event.color) ? event.color : colors[index % colors.length],
      first: chinaDay(event.start * 1000),
      last: chinaDay(event.end * 1000),
    }))
  } catch {
    if (valid) failed.value = true
  } finally {
    if (valid) loading.value = false
  }
}, { immediate: true })

function moveMonth(offset: number) {
  const { year, month: number } = monthDate.value
  const target = new Date(Date.UTC(year, number - 1 + offset, 1))
  month.value = target.toISOString().slice(0, 7)
  selected.value = ''
}

function eventDate(day: string) {
  const { year, month, date } = calendarDate(day)
  return year === monthDate.value.year ? `${month}/${date}` : `${year}/${month}/${date}`
}

function eventStatus(event: { start: number; end: number }) {
  if (props.now < event.start * 1000) return '即将开始'
  if (props.now <= event.end * 1000) return '进行中'
  return '已结束'
}
</script>

<template>
  <section class="home-card calendar-card" aria-labelledby="activity-heading">
    <div class="home-card-heading calendar-heading">
      <span class="home-card-icon calendar-icon"><UiIcon name="calendar" :size="20" /></span>
      <h2 id="activity-heading">活动日历</h2>
      <span class="month-label" aria-live="polite">{{ monthDate.year }} 年 {{ monthDate.month }} 月</span>
      <div class="month-navigation">
        <button class="home-icon-button" type="button" aria-label="上个月" @click="moveMonth(-1)"><UiIcon name="left" :size="17" /></button>
        <button v-if="month !== currentMonth" class="home-text-button today-button" type="button"
          @click="month = currentMonth; selected = ''">本月</button>
        <button class="home-icon-button" type="button" aria-label="下个月" @click="moveMonth(1)"><UiIcon name="right" :size="17" /></button>
      </div>
    </div>

    <div class="calendar-grid">
      <span v-for="weekday in weekdays" :key="weekday" class="weekday" :aria-label="'星期' + weekday">{{ weekday }}</span>
      <template v-for="(cell, index) in days" :key="index">
        <button v-if="cell" type="button" class="calendar-day" :class="{ today: cell.key === today }"
          :aria-current="cell.key === today ? 'date' : undefined" :aria-pressed="cell.key === selected"
          :aria-label="`${monthDate.month}月${cell.day}日，${cell.events.length}项活动`"
          @click="selected = selected === cell.key ? '' : cell.key">
          <span>{{ cell.day }}</span>
          <span class="day-events" aria-hidden="true">
            <span v-for="event in cell.events.slice(0, 3)" :key="event.id" :style="{ backgroundColor: event.color }"></span>
            <small v-if="cell.events.length > 3">+{{ cell.events.length - 3 }}</small>
          </span>
        </button>
        <span v-else class="calendar-blank" aria-hidden="true"></span>
      </template>
    </div>

    <div v-if="selected" class="activity-filter">
      <span>{{ eventDate(selected) }} 的活动</span>
      <button class="home-text-button" type="button" @click="selected = ''">查看全月</button>
    </div>
    <div v-if="loading || failed || !visible.length" class="home-feed-message" role="status">
      <p v-if="loading">正在加载石之家活动…</p>
      <template v-else-if="failed">
        <p>暂时无法读取石之家活动。</p>
        <button class="home-text-button" type="button" @click="request++">重试</button>
      </template>
      <p v-else>{{ selected ? '这天没有活动。' : '本月暂无官方活动。' }}</p>
    </div>
    <ul v-else class="activity-list">
      <li v-for="event in visible" :key="event.id">
        <component :is="event.url ? 'a' : 'div'" :href="event.url ?? undefined"
          :target="event.url ? '_blank' : undefined" :rel="event.url ? 'noopener noreferrer' : undefined" class="activity-row">
          <span class="activity-dot" :style="{ backgroundColor: event.color }"></span>
          <span class="activity-copy">
            <strong>{{ event.name }}</strong>
            <small>{{ event.first === event.last ? eventDate(event.first) : `${eventDate(event.first)} – ${eventDate(event.last)}` }}</small>
          </span>
          <span class="activity-status" :class="{ running: eventStatus(event) === '进行中' }">{{ eventStatus(event) }}</span>
          <UiIcon v-if="event.url" class="activity-link" name="external" :size="14" />
        </component>
      </li>
    </ul>
  </section>
</template>
