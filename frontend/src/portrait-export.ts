export function portraitPng(
  figure: HTMLImageElement | HTMLCanvasElement,
  backdrop: HTMLImageElement,
  view: 'full' | 'portrait',
) {
  const output = document.createElement('canvas')
  output.width = 880
  output.height = 1200
  const context = output.getContext('2d')
  if (!context) throw new Error('当前设备无法生成 PNG')
  drawLayer(context, backdrop, 'cover', view === 'full' ? 1 : 0.3)
  drawLayer(context, figure, view === 'full' ? 'cover' : 'contain', 0.5)
  return output.toDataURL('image/png')
}

function drawLayer(
  context: CanvasRenderingContext2D,
  image: HTMLImageElement | HTMLCanvasElement,
  fit: 'contain' | 'cover',
  vertical: number,
) {
  const sourceWidth = image instanceof HTMLImageElement ? image.naturalWidth : image.width
  const sourceHeight = image instanceof HTMLImageElement ? image.naturalHeight : image.height
  const { width, height } = context.canvas
  const scale = (fit === 'cover' ? Math.max : Math.min)(width / sourceWidth, height / sourceHeight)
  const targetWidth = sourceWidth * scale
  const targetHeight = sourceHeight * scale
  context.drawImage(image, (width - targetWidth) / 2, (height - targetHeight) * vertical, targetWidth, targetHeight)
}
