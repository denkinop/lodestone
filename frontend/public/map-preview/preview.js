const mapTileBounds = [[0, 0], [2048, 2048]]

async function renderMap() {
  const status = document.getElementById('map-status')
  const container = document.getElementById('map')
  const params = new URLSearchParams(location.hash.slice(1))
  const id = Number(params.get('id'))
  if (!Number.isSafeInteger(id) || id <= 0) {
    status.textContent = '无法识别此地图'
    return
  }

  try {
    const interactiveMap = window.YZWF.eorzeaMap
    // Avoid a startup zoom transition finishing after the entry has been positioned.
    interactiveMap.L.Map.mergeOptions({ zoomAnimation: false })
    const map = await interactiveMap.create(container)
    map.options.zoomSnap = 0.25
    const zoomRange = container.querySelector('.eorzea-map-range-container input')
    if (zoomRange) {
      zoomRange.step = '0.25'
      zoomRange.setAttribute('aria-label', '地图缩放')
    }
    await map.loadMapKey(id)

    container.querySelector('.eorzea-map-place-select-container select')?.setAttribute('aria-label', '切换地图')
    const controls = [
      ['world', '世界', '查看世界地图'],
      ['zoom-in', '+', '放大地图'],
      ['zoom-out', '−', '缩小地图'],
    ]
    for (const [action, text, label] of controls) {
      const control = container.querySelector(`button[data-action="${action}"]`)
      if (control) {
        control.textContent = text
        control.setAttribute('aria-label', label)
      }
    }

    const x = params.get('x')
    const y = params.get('y')
    const fitArea = () => map.fitBounds(mapTileBounds, { padding: [12, 12], animate: false })
    if (params.get('f') === 'mark' && x && y && Number.isFinite(Number(x)) && Number.isFinite(Number(y))) {
      const icon = interactiveMap.loader.getIconUrl('ui/icon/060000/060561.tex')
      map.addMarker(interactiveMap.simpleMarker(Number(x), Number(y), icon, map.mapInfo))
      map.setView(map.mapToLatLng2D(Number(x), Number(y)), 0, { reset: true, animate: false })
    } else {
      fitArea()
    }
    map.on('updateInfo', fitArea)
    status.hidden = true
  } catch {
    status.textContent = '地图加载失败，可以在浏览器中打开'
  }
}

void renderMap()
