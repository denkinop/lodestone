import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import { runInNewContext } from 'node:vm'
import { parse } from '@vue/compiler-sfc'
import ts from 'typescript'

test('a missing texture prevents portrait caching until a retry loads all textures', async () => {
  const component = readFileSync(new URL('../src/PreviewStage.vue', import.meta.url), 'utf8')
  const source = parse(component).descriptor.scriptSetup.content
    .replace(/^import .*$/gm, '')
    .replace("import.meta.env.BASE_URL", "'./'")
    .replace("await import('./renderer/viewer.js')", 'captureRuntime')
  const { outputText } = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  })
  const prepared = []
  const released = []
  const saved = []
  let missing = 2
  const capture = runInNewContext(outputText + `
    offscreen.value = {};
    ({ renderPortraits, retryPortraits, portraitError, portraits });
  `, {
    AbortController, crypto,
    ref: value => ({ value }),
    computed: get => ({ get value() { return get() } }),
    defineProps: () => ({ character: { id: '1', appearance: true, portraitRoot: '/portrait/' }, active: true }),
    defineEmits: () => () => {},
    watch() {}, onMounted() {}, onBeforeUnmount() {},
    prepareGamePreview: async (_, id) => {
      prepared.push(id)
      return { scene: '/scene.json', textureRoot: '/textures/', full: false, portrait: true }
    },
    releaseGamePreview: async id => released.push(id),
    saveGamePortrait: async (id, mode) => { saved.push([id, mode]); return true },
    fetch: async () => ({ ok: true, json: async () => ({ gear: [] }) }),
    captureRuntime: { Viewer: class {
      canvas = { toDataURL: () => 'data:image/png;base64,fixture' }
      setHidden() {}
      setMode() {}
      render() {}
      dispose() {}
      load(_scene, _signal, _textureUrl, requireTextures) {
        assert.equal(requireTextures, true)
        return missing
      }
    } },
  })

  await capture.renderPortraits()
  assert.match(capture.portraitError.value, /2 张贴图/)
  assert.equal(capture.portraits.value.full, undefined)
  assert.match(capture.portraits.value.portrait, /^\/portrait\/portrait\.png/)
  assert.deepEqual(saved, [])

  missing = 0
  await capture.retryPortraits()
  assert.equal(capture.portraitError.value, '')
  assert.deepEqual(released, [prepared[0]])
  assert.notEqual(prepared[0], prepared[1])
  assert.deepEqual(saved, [[prepared[1], 'full']])
})
