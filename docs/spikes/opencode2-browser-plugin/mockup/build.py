import base64, pathlib
here = pathlib.Path(__file__).parent
src = (here / "src.html").read_text()
src = src.replace("/*BASE_CSS*/", (here / "base.css.part").read_text())
def data(p): return "data:image/png;base64," + base64.b64encode((here.parent / "evidence" / p).read_bytes()).decode()
src = src.replace("{{IMG_AGENT}}", data("q2-fleet-agent-screenshot.png")).replace("{{IMG_USER}}", data("q3-user-canvas-view.png"))
(here / "agent-browser.html").write_text(src)
print(len(src), "bytes")
