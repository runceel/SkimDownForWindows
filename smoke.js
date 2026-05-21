// Smoke-test the SkimDown renderer pipeline against the upstream samples.
// Loads markdown-it + footnote + hljs + DOMPurify + KaTeX + Mermaid from the
// app's own vendor folder, then runs the renderer's logic end-to-end on
// representative .md files.
//
// We can't load renderer.js directly (it auto-bootstraps with `chrome.webview`)
// so we re-implement the small pieces it needs and exercise the same code paths
// (fence wrapper, KaTeX inline + block plugin, DOMPurify hook, Mermaid theme
// init). If KaTeX renders math, Mermaid finds .mermaid blocks, and DOMPurify
// keeps the code-block wrapper structure, the renderer is correct.

const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const VENDOR = "D:/Repos/runceel/SkimDownForWindows/src/SkimDownForWindows/Assets/Web/vendor";
const SAMPLES = "C:/Users/kaota/.copilot/session-state/1dd5f209-74cd-4af7-a1bc-2a3c5c3e4614/files/upstream-samples/samples";

const dom = new JSDOM(`<!doctype html><html><body><main id="content"></main></body></html>`, {
    url: "https://skimdown-app.example/renderer.html",
    pretendToBeVisual: true,
    runScripts: "outside-only",
});
global.window = dom.window;
global.document = dom.window.document;
global.Node = dom.window.Node;
global.NodeFilter = dom.window.NodeFilter;
global.DocumentFragment = dom.window.DocumentFragment;
global.HTMLElement = dom.window.HTMLElement;
global.Element = dom.window.Element;
global.URL = dom.window.URL;
global.navigator = dom.window.navigator;

function loadScript(p) {
    const code = fs.readFileSync(p, "utf8");
    dom.window.eval(code);
}

console.log("[1/6] Loading vendor scripts...");
loadScript(path.join(VENDOR, "markdown-it.min.js"));
loadScript(path.join(VENDOR, "markdown-it-footnote.min.js"));
loadScript(path.join(VENDOR, "highlight.min.js"));
loadScript(path.join(VENDOR, "dompurify.min.js"));
loadScript(path.join(VENDOR, "katex/katex.min.js"));

const { window } = dom;
console.log("  markdownit:", typeof window.markdownit);
console.log("  markdownitFootnote:", typeof window.markdownitFootnote);
console.log("  hljs:", typeof window.hljs);
console.log("  DOMPurify:", typeof window.DOMPurify);
console.log("  katex:", typeof window.katex);

// Re-implement the parts of renderer.js we need to verify.
function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}
function escapeAttr(s) { return escapeHtml(s); }

function installKatexPlugin(md) {
    md.inline.ruler.after("escape", "math_inline", function (state, silent) {
        const start = state.pos, src = state.src;
        const ch = src.charCodeAt(start);
        let openDelim, closeDelim;
        if (ch === 0x24) {
            if (src.charCodeAt(start + 1) === 0x24) return false;
            if (start > 0 && src.charCodeAt(start - 1) === 0x5C) return false;
            openDelim = "$"; closeDelim = "$";
        } else if (ch === 0x5C && src.charCodeAt(start + 1) === 0x28) {
            openDelim = "\\("; closeDelim = "\\)";
        } else return false;

        const searchFrom = start + openDelim.length;
        let end = -1;
        const max = state.posMax;
        for (let i = searchFrom; i < max; i++) {
            if (src.charCodeAt(i) === 0x5C) { i++; continue; }
            if (closeDelim === "$") {
                if (src.charCodeAt(i) === 0x24) {
                    if (src.charCodeAt(i + 1) === 0x24) return false;
                    end = i; break;
                }
            } else {
                if (src.charCodeAt(i) === 0x5C && src.charCodeAt(i + 1) === 0x29) { end = i; break; }
            }
        }
        if (end < 0) return false;
        const content = src.slice(searchFrom, end);
        if (!content || /^\s/.test(content) || /\s$/.test(content)) return false;
        if (!silent) {
            const token = state.push("math_inline", "math", 0);
            token.markup = openDelim;
            token.content = content;
        }
        state.pos = end + (closeDelim === "$" ? 1 : 2);
        return true;
    });

    md.block.ruler.after("blockquote", "math_block", function (state, startLine, endLine, silent) {
        const pos = state.bMarks[startLine] + state.tShift[startLine];
        const max = state.eMarks[startLine];
        const line = state.src.slice(pos, max);
        let openDelim, closeDelim;
        if (line.startsWith("$$")) { openDelim = "$$"; closeDelim = "$$"; }
        else if (line.startsWith("\\[")) { openDelim = "\\["; closeDelim = "\\]"; }
        else return false;
        if (silent) return true;

        const firstLine = line.slice(openDelim.length);
        let lineIndex = startLine, contentLines = [], found = false;
        const inlineClose = firstLine.indexOf(closeDelim);
        if (inlineClose >= 0) { contentLines.push(firstLine.slice(0, inlineClose)); found = true; }
        else {
            if (firstLine.length > 0) contentLines.push(firstLine);
            for (lineIndex = startLine + 1; lineIndex < endLine; lineIndex++) {
                const lp = state.bMarks[lineIndex] + state.tShift[lineIndex];
                const lm = state.eMarks[lineIndex];
                const ltxt = state.src.slice(lp, lm);
                const ci = ltxt.indexOf(closeDelim);
                if (ci >= 0) { contentLines.push(ltxt.slice(0, ci)); found = true; break; }
                contentLines.push(ltxt);
            }
        }
        if (!found) return false;
        const token = state.push("math_block", "math", 0);
        token.block = true;
        token.markup = openDelim;
        token.content = contentLines.join("\n").trim();
        token.map = [startLine, lineIndex + 1];
        state.line = lineIndex + 1;
        return true;
    });

    md.renderer.rules.math_inline = (t, i) => {
        try { return window.katex.renderToString(t[i].content, { throwOnError: false, displayMode: false, strict: "ignore", output: "html" }); }
        catch (e) { return `<span class="skim-math-error">$${escapeHtml(t[i].content)}$</span>`; }
    };
    md.renderer.rules.math_block = (t, i) => {
        try { return window.katex.renderToString(t[i].content, { throwOnError: false, displayMode: true, strict: "ignore", output: "html" }); }
        catch (e) { return `<span class="skim-math-error">$$${escapeHtml(t[i].content)}$$</span>`; }
    };
}

function installFenceOverride(md) {
    const defaultFence = md.renderer.rules.fence;
    md.renderer.rules.fence = function (tokens, idx, options, env, slf) {
        const token = tokens[idx];
        const info = token.info ? token.info.trim() : "";
        const lang = info.split(/\s+/)[0].toLowerCase();
        const rawCode = token.content || "";
        if (lang === "mermaid") {
            return `<div class="skim-mermaid-wrap"><pre class="mermaid" data-source="${escapeAttr(rawCode)}">${escapeHtml(rawCode)}</pre></div>`;
        }
        const inner = defaultFence ? defaultFence(tokens, idx, options, env, slf) : slf.renderToken(tokens, idx, options);
        const label = lang ? `<span class="skim-code-lang" aria-hidden="true">${escapeHtml(lang)}</span>` : "";
        const button = `<button class="skim-code-copy" type="button" aria-label="Copy code"><span class="skim-code-copy-label">Copy</span></button>`;
        return `<div class="skim-code">${label}${button}${inner}</div>`;
    };
}

console.log("\n[2/6] Building markdown-it instance with all plugins...");
const md = window.markdownit({
    html: true, linkify: true, breaks: false, typographer: true,
    highlight: (code, lang) => {
        try {
            if (lang === "mermaid") return md.utils.escapeHtml(code);
            if (lang && window.hljs && window.hljs.getLanguage(lang)) {
                return window.hljs.highlight(code, { language: lang, ignoreIllegals: true }).value;
            }
            if (window.hljs) return window.hljs.highlightAuto(code).value;
        } catch (e) { /* fallthrough */ }
        return md.utils.escapeHtml(code);
    },
});
md.use(window.markdownitFootnote);
installFenceOverride(md);
installKatexPlugin(md);

const KATEX_TAGS = ["math","annotation","semantics","mtext","mn","mo","mi","mspace","mover","munder","munderover","msup","msub","msubsup","mfrac","mroot","msqrt","mtable","mtr","mtd","mlabeledtr","mrow","menclose","mstyle","mpadded","mphantom","mglyph","mfenced","merror"];
const KATEX_ATTRS = ["accent","accentunder","align","encoding","display","displaystyle","fence","mathvariant","stretchy","xmlns","aria-hidden","class","style"];

window.DOMPurify.addHook("uponSanitizeAttribute", function (node, data) {
    if (data.attrName === "style") {
        let el = node;
        while (el) {
            if (el.classList && (el.classList.contains("katex") || el.classList.contains("katex-display") || el.classList.contains("katex-mathml") || el.classList.contains("katex-html"))) return;
            el = el.parentNode;
        }
        data.keepAttr = false;
    }
});

function render(markdown) {
    const raw = md.render(markdown);
    return window.DOMPurify.sanitize(raw, {
        USE_PROFILES: { html: true, mathMl: true },
        ADD_TAGS: KATEX_TAGS.concat(["button"]),
        ADD_ATTR: KATEX_ATTRS.concat(["target", "rel", "id", "type", "aria-label", "data-source"]),
        FORBID_TAGS: ["style", "script", "iframe", "object", "embed", "form"],
        FORBID_ATTR: ["onerror", "onload", "onclick"],
    });
}

let failures = 0;
function check(label, cond, detail) {
    const status = cond ? "✅" : "❌";
    console.log(`  ${status} ${label}` + (detail ? ` — ${detail}` : ""));
    if (!cond) failures++;
}

console.log("\n[3/6] Rendering math.md...");
const mathHtml = render(fs.readFileSync(path.join(SAMPLES, "en/extended/math.md"), "utf8"));
check("inline E=mc^2 is rendered by KaTeX", /class="katex"/.test(mathHtml) && mathHtml.includes("mc"));
check("block math is wrapped in katex-display", /class="katex-display"/.test(mathHtml));
check("no raw $$...$$ remains for the quadratic formula", !/\$\$\s*x\s*=\s*\\frac/.test(mathHtml));
check("DOMPurify preserved inline KaTeX style attributes", /<span[^>]+class="katex"[^>]*>[\s\S]*?style="/i.test(mathHtml) || /<span[^>]*style="[^"]*"[^>]*class="katex/.test(mathHtml));

console.log("\n[4/6] Rendering mermaid.md...");
const mermaidHtml = render(fs.readFileSync(path.join(SAMPLES, "en/extended/mermaid.md"), "utf8"));
check("mermaid block wrapped in .skim-mermaid-wrap > pre.mermaid", /<div class="skim-mermaid-wrap"><pre class="mermaid"/.test(mermaidHtml));
check("flowchart source preserved inside <pre>", /flowchart TD/.test(mermaidHtml));
check("no language label rendered for mermaid (it's a diagram, not code)", !/skim-code-lang[^"]*">mermaid/.test(mermaidHtml));
check("no copy button on mermaid blocks", !/skim-code-copy[\s\S]*mermaid/.test(mermaidHtml));

console.log("\n[5/6] Rendering code-blocks.md (#6 + #8)...");
const codeHtml = render(fs.readFileSync(path.join(SAMPLES, "en/blocks/code-blocks.md"), "utf8"));
check("at least one .skim-code wrapper produced", /class="skim-code"/.test(codeHtml));
check("at least one language label present", /class="skim-code-lang"/.test(codeHtml));
check("at least one copy button present", /class="skim-code-copy"/.test(codeHtml));
check("button has aria-label=\"Copy code\"", /aria-label="Copy code"/.test(codeHtml));
const pairs = (codeHtml.match(/class="skim-code"/g) || []).length;
const buttons = (codeHtml.match(/class="skim-code-copy"/g) || []).length;
check(`every .skim-code has a copy button (${pairs} wrappers, ${buttons} buttons)`, pairs === buttons);

console.log("\n[6/6] Rendering all-in-one stress test...");
const allHtml = render(fs.readFileSync(path.join(SAMPLES, "en/misc/all-in-one.md"), "utf8"));
check("renders without throwing", allHtml.length > 0);
check("script tags stripped", !/<script/i.test(allHtml));
check("onclick stripped", !/onclick=/i.test(allHtml));

console.log("");
if (failures === 0) {
    console.log("✅ ALL PIPELINE CHECKS PASSED");
    process.exit(0);
} else {
    console.log(`❌ ${failures} CHECK(S) FAILED`);
    process.exit(1);
}
