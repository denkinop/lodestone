import type { JobProgress } from './jobs'

export interface Item {
  id: number
  name: string
  gameIcon?: number
  category?: string
  rarity?: number
  job_category?: string
  attributes?: { name: string; value: number }[]
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
  id: string
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
  portraitRoot: string
  jobId: number | null
  level: number | null
  appearance: boolean
  detailError: string | null
}

export function icon(item?: Item, iconRoot?: string) {
  return item?.gameIcon && iconRoot ? `${iconRoot}${item.gameIcon}.png` : undefined
}

export function wikiUrl(item: Item) {
  return 'https://ff14.huijiwiki.com/wiki/' + encodeURIComponent('物品:' + item.name)
}
