// tool-icons.ts maps tools to Lucide Vue components; the app only uses its labels. Every icon resolves to null.
module.exports = new Proxy({}, { get: (_, name) => (name === "__esModule" ? false : null) });
