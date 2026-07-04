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

const dom = new JSDOM(`<!doctype html><html><body><div id="skim-zoom-root"><main id="content"></main></div><button id="table-of-contents-opener" class="skim-toc-opener" type="button" aria-controls="table-of-contents" aria-expanded="false" hidden>Contents</button><aside id="table-of-contents" class="skim-toc" hidden><div id="table-of-contents-title"></div><div id="table-of-contents-empty" hidden></div><nav id="table-of-contents-list"></nav></aside><div id="search-status" hidden></div></body></html>`, {
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

    // --- 2b. Table of Contents ---
    console.log("[2b] Table of Contents");
    var toc = window.document.getElementById("table-of-contents");
    var tocOpener = window.document.getElementById("table-of-contents-opener");
    var tocList = window.document.getElementById("table-of-contents-list");
    var tocEmpty = window.document.getElementById("table-of-contents-empty");
    var tocButtons = tocList ? Array.prototype.slice.call(tocList.querySelectorAll(".skim-toc-item")) : [];
    check("TOC pane is visible after rendering headings",
        toc && toc.hidden === false && window.document.body.dataset.tocVisible === "true");
    check("TOC contains one item per heading",
        tocButtons.length === 3,
        "got: " + tocButtons.length);
    check("TOC uses heading text and duplicate slug ids",
        tocButtons[0] && tocButtons[0].textContent === "Hello World" &&
        tocButtons[1] && tocButtons[1].dataset.headingId === "section-12-beta" &&
        tocButtons[2] && tocButtons[2].dataset.headingId === "section-12-beta-1");
    check("TOC opener is available when TOC is enabled",
        tocOpener && tocOpener.hidden === false && tocOpener.textContent === "Contents");
    if (tocOpener) {
        tocOpener.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true }));
        await new Promise(r => setTimeout(r, 30));
        check("TOC opener toggles the drawer open",
            window.document.body.dataset.tocDrawerOpen === "true" &&
            tocOpener.getAttribute("aria-expanded") === "true");
    }
    if (tocButtons[1]) {
        var targetHeading = window.document.getElementById("section-12-beta");
        var scrolled = false;
        targetHeading.scrollIntoView = function () { scrolled = true; };
        tocButtons[1].dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true }));
        await new Promise(r => setTimeout(r, 30));
        check("clicking a TOC item scrolls to the matching heading", scrolled);
        check("clicked TOC item becomes active",
            tocButtons[1].classList.contains("active"));
        check("clicking a TOC item closes the drawer",
            !("tocDrawerOpen" in window.document.body.dataset) &&
            tocOpener.getAttribute("aria-expanded") === "false");
    }
    postToRenderer({ type: "tocVisible", visible: false });
    await new Promise(r => setTimeout(r, 30));
    check("tocVisible=false hides the pane/opener and clears body reservation",
        toc.hidden === true &&
        tocOpener.hidden === true &&
        !("tocVisible" in window.document.body.dataset) &&
        !("tocDrawerOpen" in window.document.body.dataset));
    postToRenderer({ type: "tocVisible", visible: true });
    await new Promise(r => setTimeout(r, 30));
    check("tocVisible=true shows the pane/opener again",
        toc.hidden === false &&
        tocOpener.hidden === false &&
        window.document.body.dataset.tocVisible === "true");
    h = await renderMd("Plain paragraph only\n");
    check("TOC empty state appears for documents without headings",
        tocEmpty && tocEmpty.hidden === false && tocList.hidden === true);

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
    // The wrap/scroll split: outer wrap hosts the absolutely-positioned
    // zoom-hint badge, inner `.skim-mermaid-scroll` owns horizontal scroll.
    check("mermaid scroll inner present", /class="skim-mermaid-scroll"/.test(h));
    check("pre.mermaid present (data-source re-attached post-sanitize)",
        /<pre[^>]*class="mermaid"[^>]*data-source/.test(h));
    // After bindZoomToMermaidWraps() runs, each wrap should have the badge.
    check("zoom hint badge appended", /class="skim-mermaid-zoom-hint"/.test(h));
    check("wrap is marked role=button for click open",
        /class="skim-mermaid-wrap"[^>]*role="button"/.test(h));

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

    // --- 16. Mermaid zoom modal: setStrings + i18n fallback ---
    //
    // The Mermaid "click to enlarge" UI is renderer-side and localizable via
    // a `{ type: "strings", strings: {...} }` host message. Missing keys
    // fall back to the English defaults baked into renderer.js so a partial
    // translation never breaks the UI.
    console.log("[16] Mermaid zoom modal — i18n");
    var internal = window.__skimDownInternal;
    check("setStrings exposed on __skimDownInternal", typeof internal.setStrings === "function");
    check("t() exposed on __skimDownInternal", typeof internal.t === "function");

    // Default = English baked in.
    check("t() returns default English when no strings set",
        /enlarge/i.test(internal.t("mermaidZoom.openHint")),
        "got: " + internal.t("mermaidZoom.openHint"));

    // Send a partial localization. Unspecified keys must keep the default.
    postToRenderer({ type: "strings", strings: { "mermaidZoom.openHint": "拡大表示" } });
    await new Promise(r => setTimeout(r, 30));
    check("setStrings overrides single key",
        internal.t("mermaidZoom.openHint") === "拡大表示");
    check("missing key falls back to English default",
        /close/i.test(internal.t("mermaidZoom.close")),
        "got: " + internal.t("mermaidZoom.close"));

    // Re-render and confirm the hint text reflects the latest localization.
    h = await renderMd("```mermaid\nflowchart TD\nA-->B\n```\n");
    check("hint badge uses localized text after setStrings",
        h.indexOf("拡大表示") !== -1,
        "html snippet: " + h.substring(0, 400));

    // --- 17. Mermaid zoom modal: open / close lifecycle ---
    console.log("[17] Mermaid zoom modal — open/close");
    h = await renderMd("```mermaid\nflowchart TD\nA-->B\n```\n");
    check("zoom modal not open initially", !internal.isZoomModalOpen());

    var wrap = window.document.querySelector(".skim-mermaid-wrap");
    check("wrap exists for click simulation", !!wrap);

    if (wrap) {
        // Stand in for the mermaid-rendered SVG so the modal can clone it.
        var scrollInner = wrap.querySelector(".skim-mermaid-scroll");
        var fakeSvg = window.document.createElementNS("http://www.w3.org/2000/svg", "svg");
        fakeSvg.setAttribute("viewBox", "0 0 400 200");
        fakeSvg.setAttribute("width", "400");
        fakeSvg.setAttribute("height", "200");
        var rect = window.document.createElementNS("http://www.w3.org/2000/svg", "rect");
        rect.setAttribute("width", "100");
        rect.setAttribute("height", "100");
        fakeSvg.appendChild(rect);
        if (scrollInner) {
            // Replace the placeholder <pre> so a real SVG is present to clone.
            scrollInner.innerHTML = "";
            scrollInner.appendChild(fakeSvg);
        }

        internal.simulateMermaidWrapClick(wrap);
        await new Promise(r => setTimeout(r, 30));
        check("modal is open after simulated click", internal.isZoomModalOpen());
        check("modal DOM was appended to body",
            !!window.document.querySelector(".skim-zoom-modal"));
        check("modal contains cloned SVG",
            !!window.document.querySelector(".skim-zoom-content svg"));

        // Closing via API works.
        internal.closeZoomModal();
        await new Promise(r => setTimeout(r, 30));
        check("modal closes via closeZoomModal()", !internal.isZoomModalOpen());
    }

    // --- 18. Wheel zoom: while modal is open, document zoom must NOT change ---
    //
    // Global wheel handler intercepts Ctrl+wheel / trackpad pinch for the
    // document-level CSS `zoom`. While the modal owns the gesture, the
    // global handler must early-return so it doesn't fight the modal.
    console.log("[18] Wheel zoom isolation while modal is open");
    h = await renderMd("```mermaid\nflowchart TD\nA-->B\n```\n");
    wrap = window.document.querySelector(".skim-mermaid-wrap");
    var scrollInner2 = wrap && wrap.querySelector(".skim-mermaid-scroll");
    if (scrollInner2) {
        var fakeSvg2 = window.document.createElementNS("http://www.w3.org/2000/svg", "svg");
        fakeSvg2.setAttribute("viewBox", "0 0 400 200");
        fakeSvg2.setAttribute("width", "400");
        fakeSvg2.setAttribute("height", "200");
        scrollInner2.innerHTML = "";
        scrollInner2.appendChild(fakeSvg2);
        internal.simulateMermaidWrapClick(wrap);
        await new Promise(r => setTimeout(r, 30));
    }

    var zoomRoot = window.document.getElementById("skim-zoom-root");
    var beforeZoom = zoomRoot ? zoomRoot.style.zoom : "";
    if (internal.isZoomModalOpen()) {
        // Fire a Ctrl+wheel as if the user spun the wheel over the modal.
        // The renderer's `handleWheelZoom` runs in capture phase on window,
        // so dispatching on the modal element with bubbles:true is enough.
        var modalEl = window.document.querySelector(".skim-zoom-modal");
        var modalWheel = new window.WheelEvent("wheel", {
            ctrlKey: true,
            deltaY: -100,
            bubbles: true,
            cancelable: true,
        });
        modalEl.dispatchEvent(modalWheel);
        await new Promise(r => setTimeout(r, 30));
        var afterZoom = zoomRoot ? zoomRoot.style.zoom : "";
        check("global wheel handler does not alter #skim-zoom-root zoom while modal open",
            beforeZoom === afterZoom,
            "before=" + JSON.stringify(beforeZoom) + " after=" + JSON.stringify(afterZoom));
        internal.closeZoomModal();
    } else {
        check("modal opened for wheel-isolation test", false, "modal failed to open");
    }

    // --- 19. Mermaid wrap with internal <a> must NOT open modal ---
    //
    // Mermaid generates `<a xlink:href="URL">` for `click NODE href "URL"`
    // syntax. Clicking those should route to the host's external-link
    // handler, not open the zoom modal.
    console.log("[19] Mermaid wrap link click bypasses modal");
    h = await renderMd("```mermaid\nflowchart TD\nA-->B\n```\n");
    wrap = window.document.querySelector(".skim-mermaid-wrap");
    if (wrap) {
        var anchor = window.document.createElement("a");
        anchor.setAttribute("href", "https://example.com/");
        anchor.textContent = "Click me";
        wrap.querySelector(".skim-mermaid-scroll").appendChild(anchor);
        internal.simulateMermaidWrapClick(wrap, { target: anchor });
        await new Promise(r => setTimeout(r, 30));
        check("modal stays closed when clicking a Mermaid-internal link",
            !internal.isZoomModalOpen());
    }

    // --- 20. Mermaid font sync with body (upstream parity) ---
    //
    // The upstream macOS SkimDown feeds document.body's computed
    // font-family / font-size into mermaid.initialize so diagram labels
    // visually match the surrounding prose. We mirror that behavior in
    // the Windows renderer. Real mermaid is not loaded in jsdom, so we
    // stub window.mermaid with a recording initialize() and invoke
    // initMermaid directly to inspect the arguments.
    console.log("[20] Mermaid font sync with body");
    var capturedInit = null;
    var savedMermaid = window.mermaid;
    window.mermaid = {
        initialize: function (opts) { capturedInit = opts; },
        run: function () { return Promise.resolve(); },
    };
    try {
        internal.initMermaid("light");
        var bodyStyle = window.getComputedStyle(window.document.body);
        check("mermaid.initialize was called",
            capturedInit !== null && typeof capturedInit === "object");
        if (capturedInit) {
            check("mermaid.initialize fontFamily is NOT 'inherit' (SVG-inside inherit is unreliable)",
                capturedInit.fontFamily !== "inherit",
                "got: " + JSON.stringify(capturedInit.fontFamily));
            check("mermaid.initialize fontFamily equals body computed fontFamily",
                capturedInit.fontFamily === bodyStyle.fontFamily,
                "init=" + JSON.stringify(capturedInit.fontFamily) +
                " body=" + JSON.stringify(bodyStyle.fontFamily));
            var tv = capturedInit.themeVariables || {};
            check("themeVariables.fontFamily equals body computed fontFamily",
                tv.fontFamily === bodyStyle.fontFamily,
                "tv=" + JSON.stringify(tv.fontFamily) +
                " body=" + JSON.stringify(bodyStyle.fontFamily));
            check("themeVariables.fontSize equals body computed fontSize",
                tv.fontSize === bodyStyle.fontSize,
                "tv=" + JSON.stringify(tv.fontSize) +
                " body=" + JSON.stringify(bodyStyle.fontSize));
            check("themeVariables.fontSize is set (non-empty) so Mermaid does not fall back to its built-in default",
                typeof tv.fontSize === "string" && tv.fontSize.length > 0,
                "got: " + JSON.stringify(tv.fontSize));
        }
    } finally {
        // Restore so any later tests still see the previous (undefined) mermaid.
        if (savedMermaid === undefined) {
            delete window.mermaid;
        } else {
            window.mermaid = savedMermaid;
        }
    }

    // --- 21. Mermaid SVG stays at intrinsic 1:1 size (no max-width: 100% cap) ---
    //
    // The font sync (section 20) is only effective if the rendered SVG also
    // stays at its intrinsic 1:1 size. Capping with `max-width: 100%`
    // proportionally shrinks the whole SVG — including in-diagram text
    // driven by `themeVariables.fontSize = bodyStyle.fontSize` — whenever
    // the wrap is narrower than the diagram's natural width. Upstream macOS
    // SkimDown deliberately leaves Mermaid SVGs at intrinsic size and lets
    // the surrounding card handle overflow (see upstream comment "renders
    // at its intrinsic (1:1) size where in-diagram text matches body
    // font-size"). The Windows port keeps `normalizeMermaidSvgSizes`
    // turning percentage width into pixel width (needed so the document-
    // level CSS `zoom` scales the SVG), but the CSS must NOT re-cap the
    // SVG. Verify skimdown.css follows that policy and `.skim-mermaid-
    // scroll` keeps `overflow-x: auto` to host horizontal scrolling.
    console.log("[21] Mermaid SVG stays at intrinsic 1:1 size");
    var cssPath = path.join(ROOT, "skimdown.css");
    var cssText = fs.readFileSync(cssPath, "utf-8");
    var mermaidSvgRule = cssText.match(/main\.markdown-body\s+\.skim-mermaid-wrap\s+svg\s*\{[^}]*\}/);
    check(".skim-mermaid-wrap svg CSS rule exists in skimdown.css",
        mermaidSvgRule !== null,
        "could not locate `.skim-mermaid-wrap svg { ... }` selector");
    if (mermaidSvgRule) {
        var ruleBody = mermaidSvgRule[0];
        check(".skim-mermaid-wrap svg does NOT set max-width: 100% (which would shrink in-diagram text)",
            !/max-width\s*:\s*100\s*%/i.test(ruleBody),
            "rule body: " + ruleBody);
        check(".skim-mermaid-wrap svg uses display: block + margin auto so narrow diagrams center without text-align clipping",
            /display\s*:\s*block/i.test(ruleBody) &&
            /margin-(?:left|right|inline-(?:start|end))\s*:\s*auto/i.test(ruleBody),
            "rule body: " + ruleBody);
    }
    var scrollRule = cssText.match(/main\.markdown-body\s+\.skim-mermaid-scroll\s*\{[^}]*\}/);
    check(".skim-mermaid-scroll CSS rule exists",
        scrollRule !== null,
        "could not locate `.skim-mermaid-scroll { ... }` selector");
    if (scrollRule) {
        check(".skim-mermaid-scroll keeps `overflow-x: auto` to host horizontal scroll for wide diagrams",
            /overflow-x\s*:\s*auto/i.test(scrollRule[0]),
            "rule body: " + scrollRule[0]);
    }

    // [22] The scroll card (`.skim-mermaid-wrap`) itself must hug a narrow
    // diagram (so the card isn't a giant full-width box with a thin SVG
    // floating inside) and stay capped to the markdown body width for
    // wide diagrams (so its inner `.skim-mermaid-scroll` can take over
    // horizontal scrolling). The recipe is:
    //   width: fit-content;          /* shrink to the SVG's natural size  */
    //   max-width: 100%;             /* but never exceed the body width   */
    //   margin: 1em auto;            /* center the card in the body       */
    // Together with `.skim-mermaid-wrap svg { display:block; margin:auto }`
    // this gives: narrow TD diagram → card hugs the SVG, centered in body;
    // wide LR diagram → card spans body width, SVG scrolls inside.
    console.log("[22] Mermaid scroll card hugs the diagram and centers in body");
    var wrapRule = cssText.match(/main\.markdown-body\s+\.skim-mermaid-wrap\s*\{[^}]*\}/);
    check(".skim-mermaid-wrap CSS rule exists in skimdown.css",
        wrapRule !== null,
        "could not locate `.skim-mermaid-wrap { ... }` selector");
    if (wrapRule) {
        var wrapBody = wrapRule[0];
        check(".skim-mermaid-wrap uses `width: fit-content` so the card hugs a narrow SVG",
            /width\s*:\s*fit-content/i.test(wrapBody),
            "rule body: " + wrapBody);
        check(".skim-mermaid-wrap caps itself to the body via `max-width: 100%` so wide SVGs use inner scroll instead of overflowing the card",
            /max-width\s*:\s*100\s*%/i.test(wrapBody),
            "rule body: " + wrapBody);
        check(".skim-mermaid-wrap uses `margin: ... auto` so the card itself centers within the markdown body",
            /margin\s*:\s*[^;]*\bauto\b/i.test(wrapBody),
            "rule body: " + wrapBody);
    }

    console.log("[23] TOC responsive drawer CSS");
    var tocMediaRule = cssText.match(/@media\s*\(max-width:\s*760px\)\s*\{[\s\S]*?body\[data-toc-drawer-open="true"\]\s+\.skim-toc\s*\{[\s\S]*?\n\s*\}\n\}/);
    check("TOC drawer uses the 760px responsive breakpoint",
        tocMediaRule !== null,
        tocMediaRule ? "" : "could not locate TOC drawer media rule");
    if (tocMediaRule) {
        check("TOC drawer media rule keeps the opener visible when TOC is enabled",
            /body\[data-toc-visible="true"\]\s+\.skim-toc-opener\s*\{[\s\S]*display\s*:\s*block/i.test(tocMediaRule[0]),
            "rule: " + tocMediaRule[0]);
        check("TOC pane is off-canvas by default in narrow mode",
            /\.skim-toc\s*\{[\s\S]*transform\s*:\s*translateX/i.test(tocMediaRule[0]),
            "rule: " + tocMediaRule[0]);
        check("TOC drawer opens without hiding the pane with display:none",
            !/\.skim-toc\s*\{[^}]*display\s*:\s*none/i.test(tocMediaRule[0]) &&
            /body\[data-toc-drawer-open="true"\]\s+\.skim-toc\s*\{[\s\S]*transform\s*:\s*translateX\(0\)/i.test(tocMediaRule[0]),
            "rule: " + tocMediaRule[0]);
    }

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
