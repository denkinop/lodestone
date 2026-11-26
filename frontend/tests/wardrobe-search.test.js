import assert from 'node:assert/strict'
import test from 'node:test'
import { searchWardrobe } from '../src/bridge.ts'

test('wardrobe search carries Unicode names through the WebView request header', async (t) => {
  const response = { items: [], more: false }
  const calls = []
  globalThis.window = {
    HybridWebView: {
      async InvokeDotNet(method, args) {
        const message = JSON.stringify({ MethodName: method, ParamValues: args.map(value => JSON.stringify(value)) })
        new Headers({ 'X-Maui-Request-Body': message })
        calls.push({ method, args })
        return response
      },
    },
  }
  t.after(() => { delete globalThis.window })

  const name = '影🌙 + 100%'
  assert.deepEqual(await searchWardrobe('body', name, 1), response)
  assert.deepEqual(await searchWardrobe('body', '11111', 0), response)
  assert.deepEqual(calls, [
    { method: 'SearchWardrobe', args: ['body', encodeURIComponent(name), 1] },
    { method: 'SearchWardrobe', args: ['body', '11111', 0] },
  ])
})
