interface PreviewResources {
  scene: string
  textureRoot: string
}

declare global {
  interface Window {
    HybridWebView?: {
      InvokeDotNet<T>(method: string, args: unknown[]): Promise<T>
    }
  }
}

export async function preparePreview(characterId: number) {
  if (!window.HybridWebView) throw new Error('3D 预览需要 Windows 客户端')
  try {
    const resources = await window.HybridWebView.InvokeDotNet<PreviewResources | null>(
      'PreparePreview', [characterId],
    )
    if (resources) return resources
  } catch {
    throw new Error('无法连接到客户端预览服务')
  }
  throw new Error('此角色没有可用的预览场景')
}
