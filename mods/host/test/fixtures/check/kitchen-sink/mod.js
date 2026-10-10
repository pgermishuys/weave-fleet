function note($, text) {
  $.ui.log(text);
}

const touch = ($) => {
  $.store.set("last", $.clock.now());
};

export function register(on) {
  on("session.start", async ($, e, next) => {
    note($, `started ${$.mod.name} ${$.mod.version}`);
    $.state.set("count", 0);
    $.state.set(`seen`, 1);
    touch($);
    $.store.get("last");
    $.store.delete("last");
    $.store.keys();
    $.session.id();
    $.session.title();
    $.session.harness();
    $.session.cwd();
    $.session.surfaces();
    $.clock.after(1000, () => {});
    $.clock.every(1000, () => {});
    return next(e);
  });
  on("turn.complete", ($, e, next) => {
    $.state.get("count");
    $.ui.invalidate();
    $.ui.toast("done");
    return next(e);
  });
  on("ui.render", { component: /^Tool/i, props: { n: [1, -2.5], ok: true, none: null } }, ($, e, next) => {
    const { Page, Box } = $.ui.resolve(e);
    return Box({ children: [Page({ key: "p", path: "pages/demo.html", title: "Demo" })] });
  });
  on("ui.press", ($, e, next) => {
    $.ui.open("x");
    $.ui.close("x");
    $.ui.resolve(e);
    return next(e);
  }).catch(($, e, next) => next(e));
  on("ui.input", async (_, e, next) => next(e));
  on("ui.select", function ($, e, next) {
    return next(e);
  });
}
