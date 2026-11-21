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
  tribe: string | null
  gender: string | null
  birthday: string | null
  deity: string | null
  classJobId: number | null
  level: number | null
  detailError: string | null
  gear: GameGear[]
  portraitRoot: string
  revision: string
}

export interface GameGear {
  slot: string
  label: string
  model: number
  variant: number
  loaded: boolean
  candidates: { id: number; name: string; icon: number; details?: GameItemDetails }[]
  stains: ({ name: string; color: string | null } | null)[]
}

export interface GameItemDetails {
  category: string
  rarity: number
  itemLevel: number
  equipLevel: number
  jobCategory: string
  physicalDamage: number
  magicalDamage: number
  physicalDefense: number
  magicalDefense: number
  attributes: { name: string; value: number }[]
}

export interface GameResponse {
  selectedIndex: number | null
  accounts: { index: number; name: string }[]
  characters: GameCharacter[]
  error: string | null
  notice: string | null
  current: GameCharacter | null
  currentIndex: number | null
  cached: boolean
}

export interface CommunityStatus {
  state: string
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
export const refreshLobby = (id: string, index: number) => invokeClient<GameResponse>('RefreshLobby', [id, index])
export const chooseGameCharacter = (id: string) => invokeClient<GameResponse>('ChooseGameCharacter', [id])
export const getGameProgress = (id: string) => invokeClient<GameProgress | null>('GetGameProgress', [id])
export const cancelGameOperation = (id: string) => invokeClient<boolean>('CancelGameOperation', [id])
export const signOutGame = () => invokeClient<boolean>('SignOutGame')
export const getCommunityStatus = () => invokeClient<CommunityStatus | null>('GetCommunityStatus')
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

export async function prepareGamePreview(characterId: string, requestId: string) {
  const response = await invokeClient<{
    scene: string | null; textureRoot: string | null; iconRoot: string | null; error: string | null
    full: boolean; portrait: boolean
  }>('PrepareGamePreview', [characterId, requestId])
  if (!response.scene || !response.textureRoot || !response.iconRoot)
    throw new Error(response.error || '此角色没有可用的预览场景')
  return { scene: response.scene, textureRoot: response.textureRoot, iconRoot: response.iconRoot,
    full: response.full, portrait: response.portrait }
}

export const releaseGamePreview = (id: string) => invokeClient<boolean>('ReleaseGamePreview', [id])
export const saveGamePortrait = (id: string, mode: 'full' | 'portrait', png: string) =>
  invokeClient<boolean>('SaveGamePortrait', [id, mode, png])
