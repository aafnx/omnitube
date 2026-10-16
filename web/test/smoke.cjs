// Smoke test for the injected page script: OmniTube's own bridge (from src/Leanback.cs) plus the
// VacuumTube bundle, run in jsdom on https://www.youtube.com/tv.
//
// Checks: every VacuumTube module loads without throwing, the native-gamepad bridge reaches
// Leanback as key events, the exit hook fires, and the resolution hint uses physical pixels.
// Run: node test/smoke.cjs   (after node build.mjs)

const fs = require('node:fs')
const path = require('node:path')
const util = require('node:util')
const { JSDOM, VirtualConsole } = require('jsdom')

const root = path.resolve(__dirname, '..', '..')
const bundlePath = path.join(root, 'web', 'dist', 'vacuumtube.bundle.js')
const leanbackPath = path.join(root, 'src', 'Leanback.cs')

const failures = []
const fail = (msg) => failures.push(msg)

// ---- the bridge script, straight from the C# raw string literal
const cs = fs.readFileSync(leanbackPath, 'utf8')
const marker = 'InjectScript = """'
const start = cs.indexOf(marker)
const end = start >= 0 ? cs.indexOf('""";', start + marker.length) : -1
if (start < 0 || end < 0) {
    console.error('Could not extract InjectScript from src/Leanback.cs')
    process.exit(1)
}
const bridge = cs.slice(start + marker.length, end)
const bundle = fs.readFileSync(bundlePath, 'utf8')

// ---- page
const logs = []
const vc = new VirtualConsole()
for (const level of ['log', 'info', 'warn', 'error']) {
    vc.on(level, (...args) => logs.push({ level, text: args.map((a) => (a && a.stack) || String(a)).join(' ') }))
}
vc.on('jsdomError', (e) => logs.push({ level: 'jsdomError', text: String((e && e.stack) || e) }))

const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', {
    url: 'https://www.youtube.com/tv',
    runScripts: 'outside-only',
    pretendToBeVisual: true,
    virtualConsole: vc,
})
const w = dom.window

// what Chromium has and jsdom lacks
w.TextDecoder = util.TextDecoder
w.TextEncoder = util.TextEncoder
w.MediaSource = class { static isTypeSupported() { return true } }
if (!w.crypto.randomUUID) w.crypto.randomUUID = () => '00000000-0000-4000-8000-000000000000'
Object.defineProperty(w.screen, 'width', { value: 1920 })
Object.defineProperty(w.screen, 'height', { value: 1080 })
w.devicePixelRatio = 2 // 4K TV at 200%

try {
    w.eval(bridge + '\n;\n' + bundle)
} catch (err) {
    fail('script threw at load: ' + (err.stack || err))
}

const keydowns = []
w.document.addEventListener('keydown', (e) => keydowns.push(e.keyCode))

setTimeout(() => {
    // gamepad: A, D-pad up (after VacuumTube's async controller init)
    w.__omni(true, 32768)
    w.__omni(false, 32768)
    w.__omni(true, 32780)
    w.__omni(false, 32780)
    w.close()

    // playback: a video starts -> marker 1, pauses -> marker 0
    const video = w.document.createElement('video')
    w.document.body.appendChild(video)
    Object.defineProperty(video, 'paused', { configurable: true, value: false })
    video.dispatchEvent(new w.Event('playing'))
    setTimeout(() => {
        Object.defineProperty(video, 'paused', { configurable: true, value: true })
        video.dispatchEvent(new w.Event('pause'))
    }, 100)
}, 300)

setTimeout(() => {
    const text = logs.map((l) => `${l.level}: ${l.text}`)

    const loaded = text.find((t) => t.includes('[omnitube] VacuumTube modules loaded:'))
    if (!loaded) fail('VacuumTube modules did not report loading')

    for (const t of text.filter((t) => t.includes('VacuumTube module failed') || t.startsWith('jsdomError'))) fail(t)

    if (typeof w.__omniController?.emit !== 'function') fail('controller bridge missing (window.__omniController)')
    if (!(keydowns.includes(32768) && keydowns.includes(32780))) fail(`gamepad did not reach Leanback, keydowns: [${keydowns}]`)
    if (!text.some((t) => t.includes('__OMNITUBE_EXIT__'))) fail('exit hook did not fire on window.close()')

    const playback = text.filter((t) => t.includes('__OMNITUBE_PLAYBACK__:')).map((t) => t.slice(-1)).join('')
    if (!playback.endsWith('10')) fail(`playback markers should end with 1 then 0, got "${playback}"`)

    const res = w.h5vcc?.system?.getVideoContainerSizeOverride?.()
    if (res !== '3840x2160') fail(`resolution hint should be 3840x2160 for 1920x1080 @2x, got ${res}`)

    if (failures.length) {
        console.error('SMOKE TEST FAILED')
        for (const f of failures) console.error(' - ' + f)
        console.error('\nPage log:\n' + text.join('\n'))
        process.exit(1)
    }

    console.log(`Smoke test passed: ${loaded.replace(/^.*loaded: /, '')} modules, gamepad OK, exit OK, playback OK, resolution ${res}`)
    process.exit(0)
}, 1500)
