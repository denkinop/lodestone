export const jobGroups = [
  { label: '防护', jobs: [['pld', '骑士'], ['war', '战士'], ['drk', '暗黑骑士'], ['gnb', '绝枪战士']] },
  { label: '治疗', jobs: [['whm', '白魔法师'], ['sch', '学者'], ['ast', '占星术士'], ['sge', '贤者']] },
  { label: '输出', jobs: [
    ['mnk', '武僧'], ['drg', '龙骑士'], ['nin', '忍者'], ['sam', '武士'], ['rpr', '钐镰客'], ['vpr', '蝰蛇剑士'],
    ['brd', '吟游诗人'], ['mch', '机工士'], ['dnc', '舞者'],
    ['blm', '黑魔法师'], ['smn', '召唤师'], ['rdm', '赤魔法师'], ['pct', '绘灵法师'],
  ] },
  { label: '限定特职', jobs: [['blu', '青魔法师'], ['bst', '驯兽师']] },
  { label: '能工巧匠', jobs: [
    ['crp', '刻木匠'], ['bsm', '锻铁匠'], ['arm', '铸甲匠'], ['gsm', '雕金匠'],
    ['ltw', '制革匠'], ['wvr', '裁衣匠'], ['alc', '炼金术士'], ['cul', '烹调师'],
  ] },
  { label: '大地使者', jobs: [['min', '采矿工'], ['btn', '园艺工'], ['fsh', '捕鱼人']] },
] as const

type Job = (typeof jobGroups)[number]['jobs'][number]
export type JobId = Job[0]

export interface JobProgress {
  current: JobId | null
  levels: Partial<Record<JobId, number | null>>
}

export interface JobSyncNotice {
  message: string
  action?: 'retry' | 'login'
}

const names = new Map(jobGroups.flatMap(group => Array.from<Job>(group.jobs)))
export const jobName = (id: JobId) => names.get(id) ?? id
export const jobIcon = (id: JobId) => `${import.meta.env.BASE_URL}images/jobs/${id}.png`

const lobbyJobs: readonly (JobId | string | null)[] = [
  null, '剑术师', '格斗家', '斧术师', '枪术师', '弓箭手', '幻术师', '咒术师',
  'crp', 'bsm', 'arm', 'gsm', 'ltw', 'wvr', 'alc', 'cul', 'min', 'btn', 'fsh',
  'pld', 'mnk', 'war', 'drg', 'brd', 'whm', 'blm', '秘术师', 'smn', 'sch', '双剑师',
  'nin', 'mch', 'drk', 'ast', 'sam', 'rdm', 'blu', 'gnb', 'dnc', 'rpr', 'sge', 'vpr', 'pct', 'bst',
]

export function lobbyJobName(id: number | null) {
  const job = id === null ? null : lobbyJobs[id]
  return job ? names.get(job as JobId) ?? job : null
}

export function lobbyJobIcon(id: number | null) {
  const job = id === null ? null : lobbyJobs[id]
  return job && names.has(job as JobId) ? jobIcon(job as JobId) : undefined
}

export function communityJobProgress(levels: Record<string, number>, classJobId: number | null): JobProgress | undefined {
  const results: JobProgress['levels'] = {}
  for (const [id, name] of names) {
    if (Object.hasOwn(levels, name)) results[id] = levels[name]
  }
  if (!Object.keys(results).length) return undefined
  const job = classJobId === null ? null : lobbyJobs[classJobId]
  return { current: job && names.has(job as JobId) ? job as JobId : null, levels: results }
}
