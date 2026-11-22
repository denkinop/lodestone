import assert from 'node:assert/strict'
import test from 'node:test'
import { exportAndroidPortrait } from '../src/bridge.ts'

test('Android portrait export sends a Unicode filename through the WebView request header', async (t) => {
  const name = '拉拉菲尔🌙-神意之地-全身 100%.png'
  const png = 'data:image/png;base64,iVBORw0KGgo='
  const calls = []
  globalThis.window = {
    HybridWebView: {
      async InvokeDotNet(method, args) {
        new Headers({ 'X-Maui-Request-Body': JSON.stringify({ method, args }) })
        calls.push({ method, args })
        return true
      },
    },
  }
  t.after(() => { delete globalThis.window })

  assert.equal(await exportAndroidPortrait(name, png), true)
  const completion = calls.find(({ method }) => method === 'CompletePortraitTransfer')
  assert.ok(completion)
  assert.equal(decodeURIComponent(completion.args[3]), name)
})
