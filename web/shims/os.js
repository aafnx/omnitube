// Only hostname() is used (cast-label: the device name shown when linking a phone).
module.exports = {
    hostname: () => 'OmniTube',
    platform: () => 'win32',
    arch: () => 'x64',
    EOL: '\r\n'
}
