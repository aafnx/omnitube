// Stand-in for Electron's renderer API, so VacuumTube's page modules run unchanged inside
// OmniTube (CEF). Everything VacuumTube's main process did for them is done here, in the page.
//
// Config lives in the youtube.com localStorage (persisted in OmniTube's browser profile),
// keyed separately from YouTube's own data.

const { EventEmitter } = require('events')

const bus = new EventEmitter()
bus.setMaxListeners(100)

const STORAGE_KEY = 'omnitube.vacuumtube.config'
const VT_VERSION = __VT_VERSION__
const OMNITUBE_VERSION = __OMNITUBE_VERSION__

// mirrors VacuumTube's src/config.js defaults; window/updater/platform-specific keys are inert here
const defaults = {
    volume: 100,
    adblock: true,
    sponsorblock: false,
    sponsorblock_uuid: '',
    sponsorblock_skip_sponsor: true,
    sponsorblock_skip_selfpromo: false,
    sponsorblock_skip_interaction: false,
    sponsorblock_skip_intro: false,
    sponsorblock_skip_outro: false,
    sponsorblock_skip_preview: false,
    sponsorblock_skip_hook: false,
    sponsorblock_skip_filler: false,
    dearrow: false,
    dislikes: false,
    remove_super_resolution: false,
    hide_shorts: false,
    block_continue_watching: false,
    h264ify: false,
    h264ify_disable_webm: true,
    h264ify_disable_vp8: true,
    h264ify_disable_vp9: true,
    h264ify_disable_av1: true,
    unlock_resolution: false,
    hardware_decoding: true,
    wayland_hdr: false,
    low_memory_mode: false,
    fullscreen: true,
    features_enabled: false,
    music_mode_feature: false,
    music_mode: false,
    ultrawide_feature: false,
    remaining_time_feature: false,
    no_window_decorations: true,
    keep_on_top: false,
    pause_on_blur: false,
    userstyles: false,
    disabled_userstyles: [],
    touch_overlay: true,
    controller_support: true,
    device_discoverability: false,
    auto_update: false
}

function load() {
    let stored = {}
    try {
        stored = JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}') || {}
    } catch {
        stored = {}
    }
    return { ...defaults, ...stored }
}

let config = load()

function save() {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(config))
    } catch (err) {
        console.error('[omnitube] failed to save VacuumTube config', err)
    }
}

if (!config.sponsorblock_uuid) {
    config.sponsorblock_uuid = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/-/g, '')
    save()
}

function aboutSnapshot() {
    return {
        status: 'idle',
        currentVersion: `${VT_VERSION} (OmniTube ${OMNITUBE_VERSION})`,
        latestVersion: '',
        platform: 'win32',
        arch: 'x64',
        installationType: 'portable',
        canAutoUpdate: false,
        progress: null
    }
}

const ipcRenderer = {
    sendSync(channel, arg) {
        switch (channel) {
            case 'get-config':
                return { ...config }
            case 'set-config':
                config = { ...config, ...(arg || {}) }
                save()
                setTimeout(() => bus.emit('config-update', {}, { ...config }), 0)
                return { ...config }
            case 'can-auto-update':
                return false
            default:
                return undefined
        }
    },

    invoke(channel) {
        switch (channel) {
            case 'is-steam':
                return Promise.resolve(false) // suppresses VacuumTube's "use Steam Input" toast; OmniTube reads XInput itself
            case 'is-focused':
                return Promise.resolve(document.hasFocus())
            case 'get-userstyles':
                return Promise.resolve([])
            case 'get-deeplink':
                return Promise.resolve(null)
            case 'get-about-info':
            case 'check-for-updates':
                return Promise.resolve(aboutSnapshot())
            case 'request-microphone-permission':
                return Promise.resolve(false)
            case 'reload':
                location.reload()
                return Promise.resolve()
            default:
                return Promise.resolve(null)
        }
    },

    send() {},

    on(channel, listener) {
        bus.on(channel, listener)
        return ipcRenderer
    },

    once(channel, listener) {
        bus.once(channel, listener)
        return ipcRenderer
    },

    removeListener(channel, listener) {
        bus.removeListener(channel, listener)
        return ipcRenderer
    },

    removeAllListeners(channel) {
        bus.removeAllListeners(channel)
        return ipcRenderer
    }
}

// host -> page events ('focus', 'blur', ...), called by OmniTube.exe
window.__omniIpcEmit = (channel, ...args) => bus.emit(channel, {}, ...args)

module.exports = {
    ipcRenderer,
    shell: { openExternal: () => Promise.resolve() },
    webFrame: {}
}
