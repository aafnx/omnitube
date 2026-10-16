// Bundles VacuumTube's page-side code (src/preload) into one script that OmniTube injects into
// youtube.com/tv before the page's own scripts run (CDP Page.addScriptToEvaluateOnNewDocument).
//
// VacuumTube's sources are used unchanged from the pinned submodule; the Electron/Node APIs they
// expect are replaced with small shims (see shims/), and a few lines are patched at build time
// (listed in PATCHES — the build fails if a patch no longer applies after a submodule update).

import * as esbuild from 'esbuild'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const here = path.dirname(fileURLToPath(import.meta.url))
const vtRoot = path.resolve(here, '..', 'third_party', 'VacuumTube')
const preload = path.join(vtRoot, 'src', 'preload')
const outFile = path.resolve(here, 'dist', 'vacuumtube.bundle.js')

if (!fs.existsSync(path.join(preload, 'index.js'))) {
    console.error(`VacuumTube sources not found at ${vtRoot} (git submodule update --init?)`)
    process.exit(1)
}

const vtPackage = JSON.parse(fs.readFileSync(path.join(vtRoot, 'package.json'), 'utf8'))
const csproj = fs.readFileSync(path.resolve(here, '..', 'OmniTube.csproj'), 'utf8')
const omniVersion = (csproj.match(/<Version>([^<]+)<\/Version>/) || [])[1] || '0.0.0'

const toVirtual = (real) => '/vt/' + path.relative(vtRoot, real).split(path.sep).join('/')
const posix = (p) => p.split(path.sep).join('/')

// ---------------------------------------------------------------- virtual file system

function walk(dir, filter, out = []) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name)
        if (entry.isDirectory()) walk(full, filter, out)
        else if (filter(full)) out.push(full)
    }
    return out
}

const vfsFiles = [
    ...walk(preload, (f) => f.endsWith('.css')),
    ...walk(path.join(vtRoot, 'locale'), (f) => f.endsWith('.json')),
    path.join(vtRoot, 'assets', 'icon.png'),
].filter((f) => fs.existsSync(f))

const vfs = Object.fromEntries(vfsFiles.map((f) => [toVirtual(f), fs.readFileSync(f).toString('base64')]))

// ---------------------------------------------------------------- entry (static version of preload/index.js)

const moduleEntries = fs.readdirSync(path.join(preload, 'modules'), { withFileTypes: true })
    .filter((e) => e.isDirectory() || e.name.endsWith('.js'))
    .sort((a, b) => a.name.localeCompare(b.name))
    .map((e) => ({ name: e.name.replace(/\.js$/, ''), file: path.join(preload, 'modules', e.isDirectory() ? path.join(e.name, 'index.js') : e.name) }))

const entrySource = `
if (location.host === 'www.youtube.com' && location.pathname === '/tv' && !window.__omniVtLoaded) {
    window.__omniVtLoaded = true;
    const xhrModifiers = require(${JSON.stringify(path.join(preload, 'util', 'xhrModifiers.js'))});
    xhrModifiers.block();

    const modules = [
${moduleEntries.map((m) => `        [${JSON.stringify(m.name)}, () => require(${JSON.stringify(m.file)})],`).join('\n')}
    ];

    for (const [name, load] of modules) {
        try {
            const mod = load();
            const result = mod();
            if (result && typeof result.catch === 'function') {
                result.catch((err) => console.error('[omnitube] VacuumTube module failed:', name, err));
            }
        } catch (err) {
            console.error('[omnitube] VacuumTube module failed:', name, err);
        }
    }

    xhrModifiers.unblock();
    console.log('[omnitube] VacuumTube modules loaded: ' + modules.length);
}
`

// ---------------------------------------------------------------- build-time patches

const PATCHES = {
    // settings that only make sense for VacuumTube's own Electron window / updater / sockets
    'modules/settings/schema.js': (src) => {
        for (const key of ['fullscreen', 'no_window_decorations', 'keep_on_top', 'hardware_decoding', 'userstyles', 'device_discoverability']) {
            const needle = `key: '${key}'`
            if (!src.includes(needle)) throw new Error(`schema.js: row '${key}' not found`)
            src = src.replace(needle, `${needle}, hide: true`)
        }
        return src
    },
    // Chromium off-screen reports the screen in logical pixels (1920x1080 on a 4K TV at 2x);
    // VacuumTube's max-resolution hint must use physical pixels or YouTube caps quality at 1080p.
    'modules/h5vcc/index.js': (src) => {
        const before = src
        src = src.replace(/window\.screen\.width/g, 'Math.round(window.screen.width * (window.devicePixelRatio || 1))')
        src = src.replace(/window\.screen\.height/g, 'Math.round(window.screen.height * (window.devicePixelRatio || 1))')
        if (src === before) throw new Error('h5vcc/index.js: screen size expressions not found')
        return src
    },
}

const appliedPatches = new Set()

// ---------------------------------------------------------------- shims

const shims = {
    electron: path.join(here, 'shims', 'electron.js'),
    fs: path.join(here, 'shims', 'fs.js'),
    os: path.join(here, 'shims', 'os.js'),
    path: require.resolve('path-browserify'),
    events: require.resolve('events/'),
    'path-browserify': require.resolve('path-browserify'),
}

const replaced = {
    [path.join(preload, 'util', 'controller.js')]: path.join(here, 'shims', 'controller.js'),
    [path.join(preload, 'modules', 'h5vcc', 'dial', 'http.js')]: path.join(here, 'shims', 'dial.js'),
    [path.join(preload, 'modules', 'h5vcc', 'dial', 'server.js')]: path.join(here, 'shims', 'dial.js'),
    [path.join(preload, 'modules', 'h5vcc', 'dial', 'discover.js')]: path.join(here, 'shims', 'dial.js'),
}

const omnitubePlugin = {
    name: 'omnitube',
    setup(build) {
        build.onResolve({ filter: /^omnitube:entry$/ }, () => ({ path: 'entry', namespace: 'omnitube' }))
        build.onResolve({ filter: /^omnitube:vfs$/ }, () => ({ path: 'vfs', namespace: 'omnitube' }))

        build.onResolve({ filter: /^(electron|fs|os|path|events|path-browserify)$/ }, (args) => ({ path: shims[args.path] }))

        build.onResolve({ filter: /^\.\.?\// }, (args) => {
            if (!args.resolveDir) return undefined
            let full = path.normalize(path.resolve(args.resolveDir, args.path))
            if (!full.endsWith('.js') && fs.existsSync(full + '.js')) full += '.js'
            if (replaced[full]) return { path: replaced[full] }
            return undefined
        })

        build.onLoad({ filter: /.*/, namespace: 'omnitube' }, (args) => {
            if (args.path === 'entry') return { contents: entrySource, loader: 'js', resolveDir: preload }
            return { contents: `module.exports = ${JSON.stringify(vfs)};`, loader: 'js' }
        })

        build.onLoad({ filter: /\.js$/ }, (args) => {
            const file = path.normalize(args.path)
            if (!file.startsWith(preload + path.sep)) return undefined

            let src = fs.readFileSync(file, 'utf8')
            const rel = posix(path.relative(preload, file))
            if (PATCHES[rel]) {
                src = PATCHES[rel](src)
                appliedPatches.add(rel)
            }

            src = src
                .replace(/\b__dirname\b/g, JSON.stringify(toVirtual(path.dirname(file))))
                .replace(/\b__filename\b/g, JSON.stringify(toVirtual(file)))

            return { contents: src, loader: 'js' }
        })
    },
}

// ---------------------------------------------------------------- build

const license = fs.readFileSync(path.join(vtRoot, 'LICENSE'), 'utf8').trim()

await esbuild.build({
    stdin: { contents: `require('omnitube:entry')`, resolveDir: here, loader: 'js' },
    bundle: true,
    format: 'iife',
    platform: 'browser',
    target: 'es2022',
    outfile: outFile,
    minify: false,
    legalComments: 'none',
    logLevel: 'warning',
    banner: {
        js: `/* OmniTube: VacuumTube ${vtPackage.version} page modules (https://github.com/shy1132/VacuumTube)\n${license}\n*/`,
    },
    define: {
        'process.platform': '"win32"',
        'process.arch': '"x64"',
        'process.env.NODE_ENV': '"production"',
        __VT_VERSION__: JSON.stringify(vtPackage.version),
        __OMNITUBE_VERSION__: JSON.stringify(omniVersion),
    },
    plugins: [omnitubePlugin],
})

const missing = Object.keys(PATCHES).filter((p) => !appliedPatches.has(p))
if (missing.length) {
    console.error('Patches not applied (file missing?):', missing.join(', '))
    process.exit(1)
}

const size = fs.statSync(outFile).size
console.log(`Built ${path.relative(process.cwd(), outFile)} — VacuumTube ${vtPackage.version}, ${moduleEntries.length} modules, ${vfsFiles.length} embedded files, ${(size / 1024).toFixed(0)} KB`)
