import type { JobProgress } from './jobs'

export interface Item {
  id: number
  name: string
  icon_id?: number
  item_level?: number
  equip_level?: number
  physical_damage?: number
  magic_damage?: number
  physical_defense?: number
  magic_defense?: number
}

export interface Equipment {
  slot: string
  label: string
  model: number
  variant: number
  stains: { name: string; color: string | null }[]
  candidates: Item[]
}

export interface Character {
  id: number
  name: string
  world: string
  dc: string
  race: string
  tribe: string
  gender: string
  birthday: string | null
  deity: string | null
  gear: Equipment[]
  jobProgress?: JobProgress
}

export function asset(path: string) {
  return import.meta.env.BASE_URL + 'demo/' + path
}

export function icon(item?: Item) {
  return item?.icon_id ? asset('icons/' + item.icon_id + '.png') : undefined
}

export function portrait(id: number, mode = 'full') {
  return asset('portraits/' + id + '-' + mode + '.png')
}

export function wikiUrl(item: Item) {
  return 'https://ff14.huijiwiki.com/wiki/' + encodeURIComponent('物品:' + item.name)
}
