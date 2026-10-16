// Replaces VacuumTube's util/controller.js (which polls navigator.getGamepads in the page).
// OmniTube.exe reads the controller natively through XInput — so the Steam overlay can take
// input away from us while it is open — and feeds button events in through window.__omniController.
// Same event shape as VacuumTube: 'down' / 'up' with { code } where code = 32768 + button index.

const { EventEmitter } = require('events')

const emitter = new EventEmitter()
emitter.setMaxListeners(50)

window.__omniController = emitter

module.exports = emitter
