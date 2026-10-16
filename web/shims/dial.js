// DIAL (casting discovery) needs raw UDP/HTTP sockets, which a web page does not have.
// Linking a phone with the TV code still works without it.

class DialServer {
    constructor() {}
    start() {}
    stop() {}
}

module.exports = DialServer
module.exports.DialServer = DialServer
module.exports.listen = () => Promise.resolve()
module.exports.start = () => {}
module.exports.stop = () => {}
