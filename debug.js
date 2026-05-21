const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { JSDOM } = require("jsdom");

const VENDOR = "D:/Repos/runceel/SkimDownForWindows/src/SkimDownForWindows/Assets/Web/vendor";
const SAMPLES = "C:/Users/kaota/.copilot/session-state/1dd5f209-74cd-4af7-a1bc-2a3c5c3e4614/files/upstream-samples/samples";
const dom = new JSDOM('<!doctype html><html><body><main id="content"></main></body></html>', { url: "https://skimdown-app.example/r.html", runScripts: "outside-only", pretendToBeVisual: true });
const ctx = dom.getInternalVMContext();
vm.runInContext("globalThis.self = globalThis; globalThis.global = globalThis;", ctx);
for (const f of ["markdown-it.min.js", "markdown-it-footnote.min.js", "markdown-it-emoji.min.js", "highlight.min.js", "dompurify.min.js", "katex/katex.min.js"]) {
    vm.runInContext(fs.readFileSync(path.join(VENDOR, f), "utf8"), ctx, { filename: f });
}
require("fs").readFileSync;
const setup = fs.readFileSync(path.join(process.env.TEMP, "renderer-smoke2", "smoke2.js"), "utf8");
const start = setup.indexOf("const SETUP = String.raw`");
const end = setup.indexOf("`;\nvm.runInContext(SETUP");
const SETUP = setup.substring(start + "const SETUP = String.raw`".length, end);
vm.runInContext(SETUP, ctx, { filename: "shim.js" });
const colorHtml = vm.runInContext(`window._render(${JSON.stringify(fs.readFileSync(path.join(SAMPLES, "en/extended/color-codes.md"), "utf8"))})`, ctx);
console.log("--- color-codes.md rendered output ---");
console.log(colorHtml);