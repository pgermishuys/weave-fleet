const found = search({ namespace: "browser", limit: 3 })
let tabs
try { tabs = await tools.browser.tabs.list({}) } catch (e) { tabs = { error: String(e && e.message || e) } }
return { found: found.items.map(i => i.path), remaining: found.remaining, tabs }
