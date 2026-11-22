const hourMs = 175_000
export const weatherPeriodMs = 8 * hourMs
export const forecastDurationMs = 48 * 60 * 60 * 1000

export interface WeatherRate {
  weather: number
  upper: number
}

export interface WeatherCatalog {
  regions: { id: string; name: string; maps: { id: number; name: string; rates: WeatherRate[] }[] }[]
  weather: Record<number, { name: string; icon: string }>
}

export interface WeatherPeriod {
  start: number
  hour: number
  weather: number
  previous: number
}

export interface WeatherFilters {
  weather: ReadonlySet<number>
  previous: ReadonlySet<number>
  hours: ReadonlySet<number>
}

export function forecastTarget(timestamp: number) {
  const period = Math.floor(timestamp / weatherPeriodMs)
  const day = Math.floor(period / 3)
  // The game's seed increments for ET 00:00, 08:00 and 16:00 are 8, 16 and 0.
  const seed = day * 100 + ((period + 1) % 3) * 8
  const mixed = (seed << 11) ^ seed
  return ((mixed ^ (mixed >>> 8)) >>> 0) % 100
}

export function forecastPeriods(rates: readonly WeatherRate[], now: number, precedingPeriods = 0): WeatherPeriod[] {
  const weatherAt = (time: number) => {
    const target = forecastTarget(time)
    return rates.find(rate => target < rate.upper)!.weather
  }
  const first = (Math.floor(now / weatherPeriodMs) - precedingPeriods) * weatherPeriodMs
  let previous = weatherAt(first - weatherPeriodMs)
  const periods: WeatherPeriod[] = []
  for (let start = first; start < now + forecastDurationMs; start += weatherPeriodMs) {
    const weather = weatherAt(start)
    const hour = (start / weatherPeriodMs % 3) * 8
    periods.push({ start, hour, weather, previous })
    previous = weather
  }
  return periods
}

export function matchingWeather(periods: readonly WeatherPeriod[], filters: WeatherFilters, now: number) {
  const matches = []
  for (const period of periods) {
    if (filters.weather.size && !filters.weather.has(period.weather)) continue
    if (filters.previous.size && !filters.previous.has(period.previous)) continue

    let offset = 0
    while (offset < 8) {
      if (filters.hours.size && !filters.hours.has(period.hour + offset)) {
        offset++
        continue
      }
      const first = offset++
      while (offset < 8 && (!filters.hours.size || filters.hours.has(period.hour + offset))) offset++

      const key = period.start + first * hourMs
      const start = Math.max(now, key)
      const end = Math.min(now + forecastDurationMs, period.start + offset * hourMs)
      if (start < end) matches.push({ period, start, end, key })
    }
  }
  return matches
}
