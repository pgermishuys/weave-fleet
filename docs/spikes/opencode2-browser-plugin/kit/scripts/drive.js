const tab = await tools.browser.tabs.open({ url: "http://127.0.0.1:5453/" })
const before = await tools.browser.snapshot({ tabID: tab.id })
const ref = (role) => before.content.split("\n").find((l) => l.includes(`[${role}]`)).match(/@(e\d+)/)[1]
await tools.browser.fill({ tabID: tab.id, ref: ref("textbox"), text: "Ada" })
await tools.browser.click({ tabID: tab.id, ref: ref("button") })
const after = await tools.browser.snapshot({ tabID: tab.id })
const shot = await tools.browser.screenshot({ tabID: tab.id })
const logs = await tools.browser.console({ tabID: tab.id })
return { tab: tab.id, before: before.content, after: after.content, shot, console: logs.messages.map((m) => `${m.level}: ${m.text}`) }
