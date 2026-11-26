import assert from 'node:assert/strict'
import test from 'node:test'
import { prepareWardrobePreview, searchWardrobe } from '../src/bridge.ts'

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

test('wardrobe preview sends both dye channels and preserves an explicit undyed channel', async t => {
  const calls = []
  globalThis.window = {
    HybridWebView: {
      async InvokeDotNet(method, args) {
        new Headers({ 'X-Maui-Request-Body': JSON.stringify({ method, args }) })
        calls.push({ method, args })
        return { scene: '/scene.json', textureRoot: '/textures/' }
      },
    },
  }
  t.after(() => { delete globalThis.window })

  await prepareWardrobePreview('1', 'request', [
    { slot: 'body', itemId: 2965, name: '梦幻装', stain1: 1, stain2: 7 },
    { slot: 'head', stain1: 0 },
  ])
  assert.equal(calls[0].method, 'PrepareWardrobePreview')
  assert.deepEqual(JSON.parse(calls[0].args[2]), [
    { slot: 'body', itemId: 2965, stain1: 1, stain2: 7 },
    { slot: 'head', stain1: 0 },
  ])
})
