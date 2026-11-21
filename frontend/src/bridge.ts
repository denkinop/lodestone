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

export async function preparePreview(characterId: number) {
  try {
    const resources = await invokeClient<PreviewResources | null>('PreparePreview', [characterId])
    if (resources) return resources
  } catch {
    throw new Error('无法连接到客户端预览服务')
  }
  throw new Error('此角色没有可用的预览场景')
}
