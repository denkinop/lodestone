const chinaOffset = 8 * 60 * 60 * 1000

export const chinaDay = (timestamp: number) => new Date(timestamp + chinaOffset).toISOString().slice(0, 10)
export const twoDigits = (value: number) => String(value).padStart(2, '0')

export function calendarDate(day: string) {
  const [year, month, date] = day.split('-').map(Number)
  return { year, month, date }
}

export function eorzeaTime(timestamp: number) {
  const minutes = Math.floor(timestamp * 144 / (7 * 60_000)) % (24 * 60)
  const hour = Math.floor(minutes / 60)
  return {
    text: `${twoDigits(hour)}:${twoDigits(minutes % 60)}`,
    daylight: hour >= 6 && hour < 18,
  }
}
