interface PreviewResources {
  scene: string
  textureRoot: string
}

export interface VersionStatus {
  revision: string | null
  cached: boolean
  error: string | null
}

export interface VersionProgress {
  bytesRead: number
  totalBytes: number
}

export interface GameArea {
  id: number
  name: string
}

export interface GameProgress {
  status: string
  number: string | null
  qr: string | null
}

export interface GameCharacter {
  id: string
  name: string
  area: string
  homeWorld: string
  currentWorld: string
  race: string | null
  gender: string | null
  classJobId: number | null
  level: number | null
  detailError: string | null
  gear: { slot: string; model: number; modelType: number; variant: number; stain1: number; stain2: number }[]
}

export interface GameResponse {
  selectedIndex: number | null
  accounts: { index: number; name: string }[]
  characters: GameCharacter[]
  error: string | null
  notice: string | null
}

export interface CommunityStatus {
  state: string
  detail: string
  bindings: { character: string; world: string }[]
  jobsStatus: string
  jobProfile: { character: string; area: string; world: string; levels: Record<string, number> } | null
}

declare global {
  interface Window {
    HybridWebView?: {
      InvokeDotNet<T>(method: string, args: unknown[]): Promise<T>
    }
  }
}

function invokeClient<T>(method: string, args: unknown[] = []) {
  if (!window.HybridWebView) return Promise.reject(new Error('请在 Windows 客户端中打开'))
  return window.HybridWebView.InvokeDotNet<T>(method, args)
}

export const getLobbyVersion = () => invokeClient<number>('GetLobbyVersion')
export const setLobbyVersion = (value: number) => invokeClient<boolean>('SetLobbyVersion', [value])
export const refreshGameVersion = (id: string) => invokeClient<VersionStatus>('RefreshGameVersion', [id])
export const getGameVersionProgress = (id: string) => invokeClient<VersionProgress | null>('GetGameVersionProgress', [id])
export const cancelGameVersion = (id: string) => invokeClient<boolean>('CancelGameVersion', [id])
export const getGameAreas = () => invokeClient<{ areas: GameArea[]; error: string | null }>('GetGameAreas')
export const getSavedGameArea = () => invokeClient<number | null>('GetSavedGameArea')
export const beginGameLogin = (id: string, mode: 'phone' | 'scan' | 'saved', account: string, areaId: number, remember: boolean) =>
  invokeClient<GameResponse>('BeginGameLogin', [id, mode, account, areaId, remember])
export const selectServiceAccount = (id: string, index: number) =>
  invokeClient<GameResponse>('SelectServiceAccount', [id, index])
export const getGameProgress = (id: string) => invokeClient<GameProgress | null>('GetGameProgress', [id])
export const cancelGameOperation = (id: string) => invokeClient<boolean>('CancelGameOperation', [id])
export const signOutGame = () => invokeClient<boolean>('SignOutGame')
export const getCommunityStatus = () => invokeClient<CommunityStatus | null>('GetCommunityStatus')
export const openCommunity = () => invokeClient<boolean>('OpenCommunity')
export const readCommunityJobs = () => invokeClient<boolean>('ReadCommunityJobs')
export const selectCommunityCharacter = (id: string) => invokeClient<string | null>('SelectCommunityCharacter', [id])

export async function preparePreview(characterId: number) {
  try {
    const resources = await invokeClient<PreviewResources | null>('PreparePreview', [characterId])
    if (resources) return resources
  } catch {
    throw new Error('无法连接到客户端预览服务')
  }
  throw new Error('此角色没有可用的预览场景')
}
