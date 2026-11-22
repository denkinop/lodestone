import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import { gunzipSync } from 'node:zlib'
import { forecastDurationMs, forecastPeriods, forecastTarget, matchingWeather, weatherPeriodMs } from '../src/weather.ts'

const { weather: catalog } = JSON.parse(gunzipSync(readFileSync(new URL('../../data/exd/zh-cn.json.gz', import.meta.url))))
const southernThanalan = catalog.regions.flatMap(region => region.maps).find(map => map.id === 146)

test('the unsigned forecast roll changes only at ET weather boundaries', () => {
  const now = Date.parse('2021-01-01T00:00:00Z')
  assert.equal(forecastTarget(now), 53)
  const start = Date.parse('2026-11-22T01:06:40Z')
  assert.deepEqual([0, 1, 2, 3, 4, 5].map(index => forecastTarget(start + index * weatherPeriodMs)),
    [98, 74, 46, 18, 47, 55])
  assert.equal(forecastTarget(start + weatherPeriodMs - 1), 98)
})

test('South Thanalan uses XIVAPI rates and retains the weather before the first period', () => {
  const now = Date.parse('2026-11-22T01:18:40Z')
  const [current, next] = forecastPeriods(southernThanalan.rates, now)
  assert.equal(southernThanalan.name, '南萨纳兰')
  assert.equal(catalog.weather[current.weather].name, '薄雾')
  assert.equal(catalog.weather[next.weather].name, '晴朗')
  assert.equal(next.previous, current.weather)

  const before = forecastPeriods(southernThanalan.rates, current.start - 1)[0]
  assert.equal(current.previous, before.weather)
})

test('weather and previous weather intersect with the selected ET hours', () => {
  const start = Date.parse('2026-11-22T01:06:40Z')
  const now = start + 5 * 175_000 + 2000
  const periods = forecastPeriods(southernThanalan.rates, now)
  const [current, next] = periods
  const matches = matchingWeather(periods, {
    weather: new Set([current.weather]), previous: new Set([current.previous]), hours: new Set([21, 22, 0]),
  }, now)
  assert.equal(matches[0].start, now)
  assert.equal(matches[0].end, start + 7 * 175_000)
  assert(matches.every(({ period }) => period.weather === current.weather && period.previous === current.previous))

  const acrossMidnight = matchingWeather(periods, { weather: new Set(), previous: new Set(), hours: new Set([23, 0]) }, now)
  assert.equal(acrossMidnight[0].start, start + 7 * 175_000)
  assert.equal(acrossMidnight[0].end, next.start)
  assert.equal(acrossMidnight[1].start, next.start)
  assert.equal(acrossMidnight[1].end, next.start + 175_000)
})

test('forecast includes the in-progress period and clips the final match at 48 hours', () => {
  const now = Date.parse('2026-11-22T01:18:40Z')
  const periods = forecastPeriods(southernThanalan.rates, now)
  const last = periods.at(-1)
  const end = now + forecastDurationMs
  assert(periods[0].start < now)
  assert(last.start < end && last.start + weatherPeriodMs >= end)
  const matches = matchingWeather(periods, { weather: new Set(), previous: new Set(), hours: new Set() }, now)
  assert.equal(matches[0].start, now)
  assert.equal(matches.at(-1).end, end)
})

test('including recent periods leaves the current weather and future forecast unchanged', () => {
  const now = Date.parse('2026-11-22T01:18:40Z')
  const precedingPeriods = 8
  const forecast = forecastPeriods(southernThanalan.rates, now)
  const withHistory = forecastPeriods(southernThanalan.rates, now, precedingPeriods)

  assert.equal(withHistory[0].hour, 0)
  assert.equal(withHistory[0].start, forecast[0].start - precedingPeriods * weatherPeriodMs)
  assert.equal(withHistory[precedingPeriods - 1].weather, forecast[0].previous)
  assert.deepEqual(withHistory.slice(precedingPeriods), forecast)
})
