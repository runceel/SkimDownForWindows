// Smoke-test the SkimDown renderer end-to-end by loading the actual
// renderer.js inside jsdom and sending it the same JSON messages
// the WinUI host sends via CoreWebView2.PostWebMessageAsJson.
//
// We mock chrome.webview's event dispatch with EventTarget-style add/dispatch.
//
// Exit code 0 == all assertions passed.

const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const ROOT = path.resolve(__dirname, "src/SkimDownForWindows/Assets/Web");

// Build a synthetic HTML page with inline scripts so jsdom doesn't try to fetch.
const html = fs.readFileSync(path.join(ROOT, "renderer.html"), "utf8");

const dom = new JSDOM(`<!doctype html><html><body><main id="content"></main><div id="search-status" hidden></div></body></html>`, {
    url: "https://skimdown-app.example/renderer.html",
    runScripts: "outside-only",
    pretendToBeVisual: true,
});

const { window } = dom;

// Mock the chrome.webview bridge BEFORE loading renderer.js so the renderer
// finds it during onReady.
const incoming = []; // messages sent by renderer to host
const messageListeners = [];
window.chrome = {
    webview: {
        postMessage: function (m) { incoming.push(m); },
        addEventListener: function (type, fn) {
            if (type === "message") messageListeners.push(fn);
        },
    },
};

function loadScript(rel) {
    const p = path.join(ROOT, rel);
    const code = fs.readFileSync(p, "utf8");
    try {
        window.eval(code);
    } catch (e) {
        console.error("Loading " + rel + " threw:", e && e.message);
    }
}

// Load assets in the same order as renderer.html.
console.log("Loading vendor scripts + renderer.js...");
loadScript("vendor/markdown-it.min.js");
loadScript("vendor/markdown-it-footnote.min.js");
loadScript("vendor/markdown-it-emoji.min.js");
loadScript("vendor/markdown-it-imsize.min.js");
loadScript("vendor/highlight.min.js");
loadScript("vendor/dompurify.min.js");
loadScript("vendor/katex/katex.min.js");
loadScript("vendor/katex/auto-render.min.js");
// Skip mermaid in jsdom — it pulls in browser APIs we'd have to stub heavily;
// the renderer's `if (!window.mermaid)` early-outs make this safe.
loadScript("renderer.js");

function postToRenderer(msg) {
    messageListeners.forEach(fn => {
        try { fn({ data: msg }); } catch (e) { console.error("listener threw", e); }
    });
}

let failures = 0;
function check(label, cond, detail) {
    const status = cond ? "✅" : "❌";
    console.log(`  ${status} ${label}` + (detail ? ` — ${detail}` : ""));
    if (!cond) failures++;
}

// Wait for renderer's "ready" message.
async function waitUntilReady(timeoutMs = 5000) {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
        if (incoming.some(m => m && m.type === "ready")) return true;
        await new Promise(r => setTimeout(r, 50));
    }
    throw new Error("Timed out waiting for renderer 'ready'");
}

function lastRendered() {
    return window.document.getElementById("content").innerHTML;
}

async function renderMd(md, opts = {}) {
    incoming.length = 0;
    postToRenderer({
        type: "render",
        markdown: md,
        sourcePath: opts.sourcePath || "test.md",
        contentBaseUri: "https://skimdown-content.example/",
        theme: opts.theme || "light",
    });
    // Allow microtasks / KaTeX to settle.
    await new Promise(r => setTimeout(r, 200));
    return lastRendered();
}

async function main() {
    console.log("Waiting for renderer 'ready'...");
    await waitUntilReady();
    console.log("Renderer ready. Running checks.\n");

    // --- 1. Task lists ---
    console.log("[1] Task lists");
    let h = await renderMd("- [ ] todo\n- [x] done\n- regular item\n");
    check("task list item present", /task-list-item/.test(h),
        "got: " + h.substring(0, 300));
    check("at least one unchecked checkbox",
        /<li[^>]*class="task-list-item"[^>]*>\s*<input[^>]*type="checkbox"(?![^>]*checked)/.test(h)
        || /<input type="checkbox" disabled="">todo/.test(h),
        "got: " + h.substring(0, 300));
    check("at least one checked checkbox",
        /<input[^>]*type="checkbox"[^>]*checked[^>]*>\s*done/.test(h)
        || /<input[^>]*checked[^>]*>(\s*)done/.test(h),
        "got: " + h.substring(0, 300));
    check("UL has contains-task-list class",
        /<ul[^>]*class="[^"]*contains-task-list/.test(h));

    // --- 2. Heading anchor IDs ---
    console.log("[2] Heading anchor IDs");
    h = await renderMd("# Hello World\n\n## Section 1.2 - Beta\n\n## Section 1.2 - Beta\n");
    check("h1 gets slug id", /<h1\s+id="hello-world"/.test(h));
    check("h2 gets slug id", /<h2\s+id="section-12-beta"/.test(h));
    check("duplicate h2 gets -1 suffix", /<h2\s+id="section-12-beta-1"/.test(h));

    // --- 3. Single-tilde strikethrough ---
    console.log("[3] Single-tilde strikethrough");
    h = await renderMd("hello ~world~ done\n");
    check("single tilde produces <s>", /<s>world<\/s>/.test(h), "got: " + h);

    // --- 4. Math: ```math fenced block ---
    console.log("[4] ```math fenced block");
    h = await renderMd("```math\nx = 1 + 2\n```\n");
    check("math fence wrapped in .skim-math-block", /class="skim-math-block"/.test(h));
    check("math is rendered by KaTeX", /class="katex/.test(h));

    // --- 5. Backtick math $`...`$ ---
    console.log("[5] $`...`$ backtick math");
    h = await renderMd("inline $`x^2 + 1`$ here\n");
    check("inline backtick math becomes katex span", /class="katex/.test(h));

    // --- 6. Imsize: ![alt](src =100x200) ---
    console.log("[6] markdown-it-imsize image dimensions");
    h = await renderMd('![logo](logo.png =100x200)\n');
    check("img has width=100", /<img[^>]+width="100"/.test(h), "got: " + h);
    check("img has height=200", /<img[^>]+height="200"/.test(h), "got: " + h);

    // --- 7. KaTeX inline + block ---
    console.log("[7] KaTeX inline + display");
    h = await renderMd("inline $E = mc^2$\n\n$$\\int_0^1 x\\,dx$$\n");
    check("inline katex span present", /class="katex"/.test(h));
    check("display katex present", /class="katex-display"/.test(h));

    // --- 8. GitHub alerts ---
    console.log("[8] GitHub alerts");
    h = await renderMd("> [!WARNING]\n> Be careful\n");
    check("blockquote gets skim-alert class", /class="[^"]*skim-alert/.test(h));
    check("alert kind class present", /skim-alert-warning/.test(h));

    // --- 9. Code blocks ---
    console.log("[9] Code-block wrapper");
    h = await renderMd("```js\nconsole.log(1)\n```\n");
    check("skim-code wrapper", /class="skim-code"/.test(h));
    check("language label", /class="skim-code-lang"[^>]*>js/.test(h));
    check("copy button", /class="skim-code-copy"/.test(h));

    // --- 10. Mermaid placeholder ---
    console.log("[10] Mermaid placeholder");
    h = await renderMd("```mermaid\nflowchart TD\nA-->B\n```\n");
    check("mermaid wrapper present", /class="skim-mermaid-wrap"/.test(h));
    check("pre.mermaid with data-source", /<pre[^>]+class="mermaid"[^>]+data-source/.test(h));

    // --- 10b. normalizeMermaidSvgSizes (Chromium CSS `zoom` workaround) ---
    //
    // Mermaid v11 emits SVGs as `<svg width="100%" style="max-width: NNNpx"
    // viewBox="X Y W H">`. Chromium's CSS `zoom` (used for the host's
    // ZoomFactor) does not scale that combination, so the renderer rewrites
    // each rendered SVG to use explicit pixel width/height attributes. We
    // can't drive real mermaid inside jsdom (it needs browser-only APIs), so
    // we exercise the pure-DOM helper directly via window.__skimDownInternal.
    console.log("[10b] normalizeMermaidSvgSizes edge cases");
    var skimInternal = window.__skimDownInternal;
    check("normalizeMermaidSvgSizes exposed on window.__skimDownInternal",
        skimInternal && typeof skimInternal.normalizeMermaidSvgSizes === "function");

    if (skimInternal && typeof skimInternal.normalizeMermaidSvgSizes === "function") {
        var normalize = skimInternal.normalizeMermaidSvgSizes;
        var contentEl = window.document.getElementById("content");

        function makeWrap(svgInnerHtml) {
            contentEl.innerHTML =
                '<div class="skim-mermaid-wrap">' +
                  '<pre class="mermaid" data-processed="true">' +
                    svgInnerHtml +
                  '</pre>' +
                '</div>';
            return contentEl.querySelector(".skim-mermaid-wrap svg");
        }

        // Case 1: happy path — natural width from style, height from viewBox.
        var svg1 = makeWrap('<svg width="100%" style="max-width: 800px;" viewBox="0 0 400 200"></svg>');
        normalize(contentEl);
        check("[normalize] width attribute set to natural width",
            svg1.getAttribute("width") === "800",
            "got: " + svg1.getAttribute("width"));
        check("[normalize] height attribute = naturalWidth * (viewBoxH / viewBoxW)",
            svg1.getAttribute("height") === "400",
            "got: " + svg1.getAttribute("height"));
        check("[normalize] inline max-width is cleared",
            !/max-width/i.test(svg1.getAttribute("style") || ""),
            "style: " + svg1.getAttribute("style"));
        check("[normalize] data-skim-size-normalized marker set",
            svg1.getAttribute("data-skim-size-normalized") === "true");

        // Case 2: viewBox with negative min-x / min-y — must still be accepted.
        var svg2 = makeWrap('<svg width="100%" style="max-width: 600px;" viewBox="-10 -20 300 150"></svg>');
        normalize(contentEl);
        check("[normalize] negative viewBox min-x/min-y is valid",
            svg2.getAttribute("width") === "600" &&
            svg2.getAttribute("height") === "300",
            "got w=" + svg2.getAttribute("width") + " h=" + svg2.getAttribute("height"));

        // Case 3: style.maxWidth missing — fall back to viewBox width.
        var svg3 = makeWrap('<svg width="100%" viewBox="0 0 250 100"></svg>');
        normalize(contentEl);
        check("[normalize] falls back to viewBox width when style.maxWidth absent",
            svg3.getAttribute("width") === "250" &&
            svg3.getAttribute("height") === "100",
            "got w=" + svg3.getAttribute("width") + " h=" + svg3.getAttribute("height"));

        // Case 4: viewBox malformed AND style.maxWidth missing — skip silently.
        var svg4 = makeWrap('<svg width="100%" viewBox="0 0 not-a-number"></svg>');
        normalize(contentEl);
        check("[normalize] skips when both natural-width sources are unusable",
            svg4.getAttribute("width") === "100%" &&
            !svg4.hasAttribute("data-skim-size-normalized"),
            "w=" + svg4.getAttribute("width") + " marked=" + svg4.getAttribute("data-skim-size-normalized"));

        // Case 5: already normalized — don't re-process / overwrite.
        var svg5 = makeWrap('<svg width="123" height="45" viewBox="0 0 999 999" data-skim-size-normalized="true"></svg>');
        normalize(contentEl);
        check("[normalize] respects existing data-skim-size-normalized marker",
            svg5.getAttribute("width") === "123" &&
            svg5.getAttribute("height") === "45",
            "got w=" + svg5.getAttribute("width") + " h=" + svg5.getAttribute("height"));

        // Reset contentEl for downstream tests that re-render markdown.
        contentEl.innerHTML = "";
    }

    // --- 11. Search ---
    console.log("[11] Search");
    await renderMd("# Hello\n\nfind this word in the text.\n");
    incoming.length = 0;
    postToRenderer({ type: "search", query: "word", caseSensitive: false });
    await new Promise(r => setTimeout(r, 100));
    h = lastRendered();
    check("search result message posted",
        incoming.some(m => m && m.type === "search/result" && m.total > 0),
        "msgs: " + JSON.stringify(incoming.filter(m => m && m.type === "search/result")));
    check("mark.skim-search-hit injected", /<mark[^>]+class="skim-search-hit/.test(h));

    // --- 12. Theme ---
    console.log("[12] Theme");
    postToRenderer({ type: "theme", theme: "dark" });
    await new Promise(r => setTimeout(r, 50));
    check("body data-theme=dark", window.document.body.dataset.theme === "dark");
    postToRenderer({ type: "theme", theme: "light" });
    await new Promise(r => setTimeout(r, 50));
    check("body data-theme=light after switch", window.document.body.dataset.theme === "light");

    // --- 13. selectAll + copySelection ---
    console.log("[13] selectAll / copySelection");
    await renderMd("Hello World\n");
    incoming.length = 0;
    postToRenderer({ type: "selectAll" });
    await new Promise(r => setTimeout(r, 50));
    const sel = window.getSelection();
    check("selectAll selects something",
        sel && sel.toString().length > 0,
        "got selection: '" + (sel && sel.toString()) + "'");
    postToRenderer({ type: "copySelection" });
    await new Promise(r => setTimeout(r, 50));
    check("copySelection posts copy message",
        incoming.some(m => m && m.type === "copy" && m.text && m.text.length > 0));

    // --- 14. anchor scroll smoke ---
    console.log("[14] scrollToAnchor smoke");
    await renderMd("# Hello World\n\nbody\n");
    let errored = false;
    try {
        postToRenderer({ type: "scrollToAnchor", hash: "#hello-world" });
        await new Promise(r => setTimeout(r, 50));
    } catch (e) { errored = true; }
    check("scrollToAnchor doesn't throw", !errored);

    // --- 15. Keyboard shortcut forwarding ---
    //
    // WebView2's child HWND swallows keystrokes before WinUI's
    // KeyboardAccelerator sees them, so the renderer forwards them as
    // { type: "shortcut", id } messages. Make sure the JS keydown
    // listener turns the canonical combos into the ids the host expects.
    console.log("[15] Keyboard shortcut forwarding");

    function fireKey(opts) {
        var ev = new window.KeyboardEvent("keydown", Object.assign({
            bubbles: true,
            cancelable: true,
            ctrlKey: true,
        }, opts));
        // jsdom's defaultPrevented surfaces preventDefault() calls inside
        // the renderer handler — we verify the handler claimed the key.
        var target = (opts && opts._target) ? opts._target : window.document.body;
        target.dispatchEvent(ev);
        return ev;
    }

    var shortcutCases = [
        { combo: { key: "f" },                        id: "find" },
        { combo: { key: "F" },                        id: "find" },          // caps-lock / shift normalization
        { combo: { key: "g" },                        id: "find-next" },
        { combo: { key: "G", shiftKey: true },        id: "find-prev" },
        { combo: { key: "e" },                        id: "use-selection-for-find" },
        { combo: { key: "b" },                        id: "toggle-sidebar" },
        { combo: { key: "o" },                        id: "open-folder" },
        { combo: { key: "w" },                        id: "close-window" },
        { combo: { key: "n" },                        id: "new-window" },
        { combo: { key: "m" },                        id: "minimize" },
        { combo: { key: "a" },                        id: "select-all" },
        { combo: { key: "0" },                        id: "zoom-reset" },
        { combo: { key: "=" },                        id: "zoom-in" },
        { combo: { key: "+", shiftKey: true },        id: "zoom-in" },       // Shift+= → "+"
        { combo: { key: "+" },                        id: "zoom-in" },       // numpad add
        { combo: { key: ";" },                        id: "zoom-in" },       // JIS keyboard: ';' is the '+' key position, so Ctrl+; (no Shift) zooms in
        { combo: { key: "-" },                        id: "zoom-out" },
    ];

    for (var i = 0; i < shortcutCases.length; i++) {
        var c = shortcutCases[i];
        incoming.length = 0;
        var ev = fireKey(c.combo);
        var msg = incoming.find(m => m && m.type === "shortcut");
        check(
            "Ctrl+" + (c.combo.shiftKey ? "Shift+" : "") + c.combo.key + " → " + c.id,
            !!msg && msg.id === c.id && ev.defaultPrevented,
            "got: " + JSON.stringify(msg) + " prevented=" + ev.defaultPrevented);
    }

    // Ctrl+C is intentionally NOT forwarded (native browser copy is faster).
    incoming.length = 0;
    var copyEv = fireKey({ key: "c" });
    check("Ctrl+C is not forwarded (native browser copy wins)",
        !incoming.some(m => m && m.type === "shortcut") && !copyEv.defaultPrevented);

    // Plain letters without Ctrl must NOT be forwarded.
    incoming.length = 0;
    var plainEv = new window.KeyboardEvent("keydown", { key: "f", bubbles: true, cancelable: true });
    window.document.body.dispatchEvent(plainEv);
    check("Plain 'f' (no Ctrl) is ignored",
        !incoming.some(m => m && m.type === "shortcut") && !plainEv.defaultPrevented);

    // Alt+Ctrl combos must NOT be forwarded (Alt+ is reserved for menu mnemonics).
    incoming.length = 0;
    var altEv = fireKey({ key: "f", altKey: true });
    check("Ctrl+Alt+F is ignored",
        !incoming.some(m => m && m.type === "shortcut") && !altEv.defaultPrevented);

    // Keystrokes inside an editable element must be left alone so the user
    // can still type. Renderer has no inputs today but the guard is real.
    var input = window.document.createElement("input");
    input.type = "text";
    window.document.body.appendChild(input);
    incoming.length = 0;
    var inInputEv = fireKey({ key: "f", _target: input });
    check("Ctrl+F inside <input> is not hijacked",
        !incoming.some(m => m && m.type === "shortcut") && !inInputEv.defaultPrevented);
    window.document.body.removeChild(input);

    console.log("");
    if (failures === 0) {
        console.log("✅ ALL RENDERER SMOKE CHECKS PASSED");
        process.exit(0);
    } else {
        console.log(`❌ ${failures} CHECK(S) FAILED`);
        process.exit(1);
    }
}

main().catch(e => {
    console.error("FATAL:", e);
    process.exit(2);
});

