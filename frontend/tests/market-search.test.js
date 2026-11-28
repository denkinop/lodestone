import assert from 'node:assert/strict'
import test from 'node:test'
import { getMarketPrices, getRecentMarketItems, searchMarketItems } from '../src/bridge.ts'

test('market requests preserve Unicode arguments in the HybridWebView request header', async t => {
  const calls = []
  globalThis.window = {
    HybridWebView: {
      async InvokeDotNet(method, args) {
        const message = JSON.stringify({ MethodName: method, ParamValues: args.map(value => JSON.stringify(value)) })
        const headers = new Headers({ 'X-Maui-Request-Body': message })
        const request = JSON.parse(headers.get('X-Maui-Request-Body'))
        calls.push([request.MethodName, ...request.ParamValues.map(value => {
          const arg = JSON.parse(value)
          return typeof arg === 'string' ? decodeURIComponent(arg) : arg
        })])
      },
    },
  }
  t.after(() => { delete globalThis.window })

  await searchMarketItems('魔导')
  await searchMarketItems('影🌙 + 100%')
  await searchMarketItems('13114')
  await getRecentMarketItems(0, '陆行鸟')
  await getMarketPrices(0, '莫古力', 13114)
  await getMarketPrices(1042, null, 13114)
  assert.deepEqual(calls, [
    ['SearchMarketItems', '魔导'],
    ['SearchMarketItems', '影🌙 + 100%'],
    ['SearchMarketItems', '13114'],
    ['GetRecentMarketItems', 0, '陆行鸟'],
    ['GetMarketPrices', 0, '莫古力', 13114],
    ['GetMarketPrices', 1042, null, 13114],
  ])
})
