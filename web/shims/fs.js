// Read-only in-memory file system holding the files VacuumTube's modules read at runtime
// (stylesheets, locale JSON, the about-page icon). Built by build.mjs as base64.

const path = require('path-browserify')
const files = require('omnitube:vfs')

const norm = (p) => path.normalize(String(p)).replace(/\/+$/, '') || '/'

function enoent(p) {
    const err = new Error(`ENOENT: no such file or directory, '${p}'`)
    err.code = 'ENOENT'
    return err
}

function decodeUtf8(b64) {
    const bin = atob(b64)
    const bytes = new Uint8Array(bin.length)
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i)
    return new TextDecoder('utf-8').decode(bytes)
}

function makeBuffer(b64) {
    return {
        toString(encoding) {
            return encoding === 'base64' ? b64 : decodeUtf8(b64)
        }
    }
}

function readFileSync(p, options) {
    const key = norm(p)
    if (!(key in files)) throw enoent(key)
    const encoding = typeof options === 'string' ? options : options && options.encoding
    return encoding ? makeBuffer(files[key]).toString(encoding) : makeBuffer(files[key])
}

function isDirectory(key) {
    const prefix = key === '/' ? '/' : key + '/'
    return Object.keys(files).some((f) => f.startsWith(prefix))
}

function existsSync(p) {
    const key = norm(p)
    return key in files || isDirectory(key)
}

function readdirSync(p, options) {
    const key = norm(p)
    const prefix = key === '/' ? '/' : key + '/'
    const entries = new Map()

    for (const f of Object.keys(files)) {
        if (!f.startsWith(prefix)) continue
        const rest = f.slice(prefix.length)
        const slash = rest.indexOf('/')
        const name = slash === -1 ? rest : rest.slice(0, slash)
        const dir = slash !== -1
        entries.set(name, entries.get(name) || dir)
    }

    if (entries.size === 0 && !isDirectory(key)) throw enoent(key)

    const names = [...entries.keys()].sort()
    if (options && options.withFileTypes) {
        return names.map((name) => ({
            name,
            isDirectory: () => entries.get(name),
            isFile: () => !entries.get(name)
        }))
    }
    return names
}

function statSync(p) {
    const key = norm(p)
    if (key in files) return { isDirectory: () => false, isFile: () => true }
    if (isDirectory(key)) return { isDirectory: () => true, isFile: () => false }
    throw enoent(key)
}

const wrap = (fn) => (...args) => new Promise((resolve, reject) => {
    try {
        resolve(fn(...args))
    } catch (err) {
        reject(err)
    }
})

module.exports = {
    readFileSync,
    existsSync,
    readdirSync,
    statSync,
    writeFileSync() {},
    mkdirSync() {},
    promises: {
        readFile: wrap(readFileSync),
        readdir: wrap(readdirSync),
        stat: wrap(statSync),
        access: wrap((p) => { if (!existsSync(p)) throw enoent(p) })
    }
}
