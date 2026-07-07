/* SkimDown WebView2 renderer.
 *
 * Bridge: receives JSON messages from the WinUI host via
 * `chrome.webview.postMessage`. Sends responses (link clicks, search counts,
 * copy fallbacks) back the same way.
 *
 * Message in:
 *   { type: "render", markdown, sourcePath, contentBaseUri,
 *                     theme, themeType, themeIsDark, themeVars }
 *   { type: "theme",  theme, themeType, themeIsDark, themeVars } // theme: "system"|"light"|"dark"|"custom"
 *   { type: "zoom",   factor }           // 0.5..3.0
 *   { type: "contentMaxWidth", value }   // CSS max-width: "760px"|"960px"|"1200px"|"none"
 *   { type: "tocVisible", visible }      // show/hide renderer-side Table of Contents
 *   { type: "search", query, caseSensitive }
 *   { type: "search/next" } / { type: "search/prev" } / { type: "search/clear" }
 *
 *   themeVars (optional): { "--skim-bg": "#...", ... } for custom user themes.
 *                         Only keys starting with "--skim-" are honored.
 *   themeIsDark (optional bool): selects dark code-highlight CSS + Mermaid dark theme.
 *
 * Message out:
 *   { type: "link",   href, kind }
 *   { type: "search/result", total, current }
 *   { type: "ready" }
 *   { type: "copy",   text }   // fallback when navigator.clipboard fails
 *   { type: "shortcut", id }   // keyboard accelerator forwarded from WebView2
 *                              // because WebView2's child HWND swallows keys
 *                              // before WinUI's KeyboardAccelerator sees them.
 */

(function () {
    "use strict";

    var md = null;
    var contentEl = null;
    var tocEl = null;
    var tocOpenerEl = null;
    var tocTitleEl = null;
    var tocEmptyEl = null;
    var tocListEl = null;
    var currentSourceDir = "";
    var currentContentBaseUri = "";
    var lastRenderedMarkdown = "";
    var lastRenderedHtml = "";
    var currentTheme = "light";       // "light" | "dark" | "custom"
    var currentThemeType = "light";   // "light" | "dark" — drives hljs + Mermaid choice
    var currentThemeIsDark = false;
    var appliedCustomVars = [];       // CSS variable names currently set on documentElement
    var mermaidReady = false;
    var tableOfContentsVisible = true;
    var hasRenderedDocument = false;
    var currentTocItems = [];
    var activeHeadingID = "";
    var activeHeadingFrameRequest = 0;
    var tocDrawerOpen = false;

    // ----- Zoom state -----
    // Local mirror of the host's AppSettings.ZoomFactor. Kept in sync via:
    //   - host -> renderer:  { type: "zoom", factor }  (menu / keyboard / restore on startup)
    //   - renderer -> host:  { type: "zoomChanged", factor }  (Ctrl+wheel / trackpad pinch)
    // Range matches the host clamp: [0.5, 3.0].
    var currentZoom = 1.0;
    var ZOOM_MIN = 0.5;
    var ZOOM_MAX = 3.0;
    // Debounce timer for posting "zoomChanged" back to the host. We apply the
    // zoom locally on every wheel tick for smooth UX, but only notify the host
    // (which triggers a settings disk write) once the user has paused.
    var zoomPostDebounceMs = 300;
    var zoomPostTimer = 0;
    var zoomPendingFactor = null;

    // ----- Search state -----
    var search = {
        query: "",
        caseSensitive: false,
        hits: [],          // Array<HTMLElement>
        current: -1,
    };

    // ----- Localization -----
    // English defaults so the renderer remains usable even before the host
    // has had a chance to post a "strings" message. The host (MainPage) reads
    // localized values from Resources.resw and posts a flat dictionary that
    // setStrings() merges over these defaults. Keys are namespaced (dotted)
    // to leave room for future feature areas.
    var DEFAULT_STRINGS = {
        "mermaidZoom.openHint": "\u2922 Click to enlarge",
        "mermaidZoom.dialogLabel": "Mermaid diagram zoomed view",
        "mermaidZoom.zoomIn": "Zoom in",
        "mermaidZoom.zoomOut": "Zoom out",
        "mermaidZoom.reset": "Reset",
        "mermaidZoom.close": "Close",
        "mermaidZoom.hint": "Wheel: zoom \u00B7 Drag: pan \u00B7 Esc: close",
        "tableOfContents.title": "Contents",
        "tableOfContents.empty": "No headings",
    };
    var currentStrings = {};
    for (var __k in DEFAULT_STRINGS) {
        if (Object.prototype.hasOwnProperty.call(DEFAULT_STRINGS, __k)) {
            currentStrings[__k] = DEFAULT_STRINGS[__k];
        }
    }

    function t(key) {
        if (typeof key !== "string") return "";
        if (Object.prototype.hasOwnProperty.call(currentStrings, key)) {
            return currentStrings[key];
        }
        if (Object.prototype.hasOwnProperty.call(DEFAULT_STRINGS, key)) {
            return DEFAULT_STRINGS[key];
        }
        return key;
    }

    function setStrings(strings) {
        if (!strings || typeof strings !== "object") return;
        for (var key in strings) {
            if (!Object.prototype.hasOwnProperty.call(strings, key)) continue;
            if (typeof key !== "string" || key.length === 0) continue;
            var value = strings[key];
            if (typeof value !== "string" || value.length === 0) continue;
            currentStrings[key] = value;
        }
        applyStringsToZoomModal();
        applyStringsToZoomHints();
        applyStringsToTableOfContents();
    }

    // ----- Mermaid zoom modal state -----
    var zoomModal = null;          // root .skim-zoom-modal element
    var zoomStage = null;          // inner stage that owns pointer / wheel input
    var zoomContent = null;        // SVG host (transformed)
    var zoomInfo = null;           // % readout
    var zoomBtnIn = null;
    var zoomBtnOut = null;
    var zoomBtnReset = null;
    var zoomBtnClose = null;
    var zoomHintEl = null;         // bottom hint text
    var zoomLastFocused = null;    // element to restore focus to on close
    var zoomState = {
        scale: 1,
        tx: 0,
        ty: 0,
    };
    var zoomPointers = new Map(); // pointerId -> {x, y, startTx, startTy}
    var zoomPinch = null;          // { startDist, startScale, startMidX, startMidY, startTx, startTy }
    var ZOOM_MODAL_MIN = 0.2;
    var ZOOM_MODAL_MAX = 8.0;

    function isZoomModalOpen() {
        return !!(zoomModal && zoomModal.classList.contains("open"));
    }

    function postToHost(payload) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(payload);
            }
        } catch (e) {
            console.warn("postMessage failed", e);
        }
    }

    function logToHost(message) {
        postToHost({ type: "log", text: String(message) });
    }

    // Surface any uncaught errors back to the C# host.
    window.addEventListener("error", function (ev) {
        logToHost("window.onerror: " + (ev.message || "?") + " at " + (ev.filename || "?") + ":" + (ev.lineno || 0));
    });
    window.addEventListener("unhandledrejection", function (ev) {
        logToHost("unhandledrejection: " + (ev.reason && ev.reason.message ? ev.reason.message : String(ev.reason)));
    });

    // ----- KaTeX inline plugin (covers $...$, $$...$$, \(...\), \[...\]) -----
    //
    // We author a tiny markdown-it plugin in-process rather than bundling
    // markdown-it-katex / markdown-it-texmath, so we can pin the exact set of
    // delimiters the SPEC asks for and keep the bundle small.
    function installKatexPlugin(mdInstance) {
        if (!window.katex) return;

        // Inline rule: $...$ and \(...\)
        mdInstance.inline.ruler.after("escape", "math_inline", function (state, silent) {
            var start = state.pos;
            var src = state.src;
            var ch = src.charCodeAt(start);
            var openDelim, closeDelim;

            if (ch === 0x24 /* $ */) {
                // Disallow $$ as inline (block path handles it).
                if (src.charCodeAt(start + 1) === 0x24) return false;
                // Escaped $ -> not math.
                if (start > 0 && src.charCodeAt(start - 1) === 0x5C /* \ */) return false;
                openDelim = "$";
                closeDelim = "$";
            } else if (ch === 0x5C /* \ */ && src.charCodeAt(start + 1) === 0x28 /* ( */) {
                openDelim = "\\(";
                closeDelim = "\\)";
            } else {
                return false;
            }

            var openLen = openDelim.length;
            var searchFrom = start + openLen;
            var end = -1;
            var max = state.posMax;
            // Find the closing delimiter (closeDelim) that isn't preceded by a backslash.
            for (var i = searchFrom; i < max; i++) {
                if (src.charCodeAt(i) === 0x5C) { i++; continue; }
                if (closeDelim === "$") {
                    if (src.charCodeAt(i) === 0x24) {
                        // Skip $$ that would be a block.
                        if (src.charCodeAt(i + 1) === 0x24) { return false; }
                        end = i;
                        break;
                    }
                } else { // \)
                    if (src.charCodeAt(i) === 0x5C && src.charCodeAt(i + 1) === 0x29) {
                        end = i;
                        break;
                    }
                }
            }
            if (end < 0) return false;

            var content = src.slice(searchFrom, end);
            // Empty math, or content starting/ending with whitespace inside $...$, is not math
            // (matches CommonMark-ish heuristics so $x$ works but `cost = $5 and $10` does not).
            if (!content || /^\s/.test(content) || /\s$/.test(content)) {
                return false;
            }

            if (!silent) {
                var token = state.push("math_inline", "math", 0);
                token.markup = openDelim;
                token.content = content;
            }
            state.pos = end + (closeDelim === "$" ? 1 : 2);
            return true;
        });

        // Block rule: $$...$$ and \[...\]
        mdInstance.block.ruler.after("blockquote", "math_block", function (state, startLine, endLine, silent) {
            var pos = state.bMarks[startLine] + state.tShift[startLine];
            var max = state.eMarks[startLine];
            var line = state.src.slice(pos, max);
            var openDelim, closeDelim;
            if (line.startsWith("$$")) {
                openDelim = "$$";
                closeDelim = "$$";
            } else if (line.startsWith("\\[")) {
                openDelim = "\\[";
                closeDelim = "\\]";
            } else {
                return false;
            }

            if (silent) return true;

            var firstLineContent = line.slice(openDelim.length);
            var lineIndex = startLine;
            var contentLines = [];
            var found = false;

            // Same-line close: $$ x $$
            var inlineCloseIdx = firstLineContent.indexOf(closeDelim);
            if (inlineCloseIdx >= 0) {
                contentLines.push(firstLineContent.slice(0, inlineCloseIdx));
                found = true;
            } else {
                if (firstLineContent.length > 0) {
                    contentLines.push(firstLineContent);
                }
                for (lineIndex = startLine + 1; lineIndex < endLine; lineIndex++) {
                    var lp = state.bMarks[lineIndex] + state.tShift[lineIndex];
                    var lm = state.eMarks[lineIndex];
                    var ltxt = state.src.slice(lp, lm);
                    var closeIdx = ltxt.indexOf(closeDelim);
                    if (closeIdx >= 0) {
                        contentLines.push(ltxt.slice(0, closeIdx));
                        found = true;
                        break;
                    }
                    contentLines.push(ltxt);
                }
            }

            if (!found) return false;

            var token = state.push("math_block", "math", 0);
            token.block = true;
            token.markup = openDelim;
            token.content = contentLines.join("\n").trim();
            token.map = [startLine, lineIndex + 1];
            state.line = lineIndex + 1;
            return true;
        });

        mdInstance.renderer.rules.math_inline = function (tokens, idx) {
            return renderKatex(tokens[idx].content, false);
        };
        mdInstance.renderer.rules.math_block = function (tokens, idx) {
            return renderKatex(tokens[idx].content, true);
        };
    }

    function renderKatex(tex, displayMode) {
        try {
            return window.katex.renderToString(tex, {
                throwOnError: false,
                displayMode: displayMode,
                strict: "ignore",
                output: "html",
            });
        } catch (e) {
            // Fallback to the original text per SPEC.
            var prefix = displayMode ? "$$" : "$";
            return "<span class=\"skim-math-error\">" + escapeHtml(prefix + tex + prefix) + "</span>";
        }
    }

    // ----- Single-tilde strikethrough (~text~) — GitHub extension on top of GFM ~~ -----
    function installSingleTildeStrike(mdInstance) {
        mdInstance.inline.ruler.after("strikethrough", "single_tilde_strikethrough", function (state, silent) {
            var src = state.src;
            var pos = state.pos;
            var max = state.posMax;
            if (src.charCodeAt(pos) !== 0x7E) return false; // '~'
            // Must be single ~, not ~~
            if (pos + 1 <= max && src.charCodeAt(pos + 1) === 0x7E) return false;
            // Find closing single ~ within the inline boundary.
            var end = pos + 1;
            var idx = src.indexOf("~", end);
            if (idx < 0 || idx > max) return false;
            end = idx;
            if (end <= pos + 1) return false;
            // Closing ~ must also be single (not adjacent to another ~).
            if (end + 1 <= max && src.charCodeAt(end + 1) === 0x7E) return false;
            if (end > 0 && src.charCodeAt(end - 1) === 0x7E) return false;
            var inner = src.slice(pos + 1, end);
            if (!inner.trim() || inner.indexOf("\n") >= 0) return false;
            if (!silent) {
                var open = state.push("s_open", "s", 1); open.markup = "~";
                var text = state.push("text", "", 0); text.content = inner;
                var close = state.push("s_close", "s", -1); close.markup = "~";
            }
            state.pos = end + 1;
            return true;
        });
    }

    // ----- Code-fence override (language label + copy button + Mermaid bypass + ```math``` -> KaTeX) -----
    function installFenceOverride(mdInstance) {
        var defaultFence = mdInstance.renderer.rules.fence;

        mdInstance.renderer.rules.fence = function (tokens, idx, options, env, slf) {
            var token = tokens[idx];
            var info = token.info ? token.info.trim() : "";
            var lang = info.split(/\s+/)[0].toLowerCase();
            var rawCode = token.content || "";

            if (lang === "mermaid") {
                // Emit a placeholder Mermaid block; we render it post-sanitize.
                // The inner `.skim-mermaid-scroll` owns the horizontal scroll so
                // that the outer `.skim-mermaid-wrap` can host an absolutely-
                // positioned zoom-hint badge that stays visible regardless of
                // the diagram's intrinsic width.
                return '<div class="skim-mermaid-wrap">' +
                       '<div class="skim-mermaid-scroll">' +
                       '<pre class="mermaid" data-source="' + escapeAttr(rawCode) + '">' +
                       escapeHtml(rawCode) +
                       '</pre></div></div>';
            }

            if (lang === "math") {
                // GitHub renders ```math fenced blocks as display KaTeX. Match that.
                return '<div class="skim-math-block">' + renderKatex(rawCode, true) + '</div>';
            }

            var inner = defaultFence
                ? defaultFence(tokens, idx, options, env, slf)
                : slf.renderToken(tokens, idx, options);

            var label = lang
                ? '<span class="skim-code-lang" aria-hidden="true">' + escapeHtml(lang) + '</span>'
                : '';
            var button = '<button class="skim-code-copy" type="button" aria-label="Copy code">' +
                         '<span class="skim-code-copy-label">Copy</span>' +
                         '</button>';

            return '<div class="skim-code">' + label + button + inner + '</div>';
        };
    }

    function escapeAttr(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c];
        });
    }

    function ensureMarkdown() {
        if (md) return md;
        md = window.markdownit({
            html: true,
            linkify: true,
            breaks: false,
            typographer: true,
            highlight: function (code, lang) {
                try {
                    if (lang === "mermaid") {
                        // Mermaid blocks bypass syntax highlighting; the fence
                        // override above re-renders them as <pre class="mermaid">.
                        return md.utils.escapeHtml(code);
                    }
                    if (lang && window.hljs && window.hljs.getLanguage(lang)) {
                        return window.hljs.highlight(code, { language: lang, ignoreIllegals: true }).value;
                    }
                    if (window.hljs) {
                        return window.hljs.highlightAuto(code).value;
                    }
                } catch (e) { /* fall through */ }
                return md.utils.escapeHtml(code);
            },
        });
        if (window.markdownitFootnote) {
            md.use(window.markdownitFootnote);
        }
        // GitHub-style emoji shortcodes (:smile:, :+1:, etc.). The bundle
        // ships the "full" GitHub set; unknown shortcodes pass through as-is.
        if (window.markdownitEmoji) {
            md.use(window.markdownitEmoji);
        }
        // GitHub-style image size syntax: `![alt](src =200x300)` / `=100x`.
        var imsize = window.markdownitImsize || window["markdown-it-imsize.js"];
        if (imsize) {
            md.use(imsize);
        }
        installFenceOverride(md);
        installKatexPlugin(md);
        installSingleTildeStrike(md);
        return md;
    }

    function initMermaid(themeType) {
        if (!window.mermaid) return;
        // Read the active --skim-* values so Mermaid label/edge colors match
        // the current page (including any custom theme overrides).
        // We sample document.body because that's where the dark/light selectors
        // resolve and where custom theme overrides are now applied (see
        // applyThemeVars). Using documentElement here would miss body-level
        // overrides and feed Mermaid the wrong palette.
        var bodyStyle = window.getComputedStyle(document.body);
        function cssVar(name) {
            var v = bodyStyle.getPropertyValue(name);
            return v ? v.trim() : "";
        }
        var bg = cssVar("--skim-bg");
        var fg = cssVar("--skim-fg");
        var soft = cssVar("--skim-soft-strong");
        var accent = cssVar("--skim-link");
        var border = cssVar("--skim-border");
        // Match Mermaid's font-family and font-size to the body so diagram
        // labels appear the same size as the surrounding prose. SVG-inside
        // `font-family: inherit` is not reliably resolved by Mermaid v11's
        // generated `<svg>` (its inheritance chain is detached from <body>),
        // so we feed the concrete computed values directly. CSS `zoom` on
        // #skim-zoom-root scales the SVG visually without altering these
        // computed values, so this stays in sync with the user zoom level.
        // Mirrors upstream macOS SkimDown (07JP27/SkimDown) renderer.js.
        var themeVariables = {
            fontFamily: bodyStyle.fontFamily,
            fontSize: bodyStyle.fontSize,
        };
        if (bg) { themeVariables.background = bg; }
        if (soft) { themeVariables.primaryColor = soft; }
        if (fg) {
            themeVariables.primaryTextColor = fg;
            themeVariables.secondaryTextColor = fg;
            themeVariables.tertiaryTextColor = fg;
        }
        if (border) { themeVariables.primaryBorderColor = border; }
        if (accent) { themeVariables.lineColor = accent; }

        // For built-in light/dark we keep Mermaid's "default" / "dark" presets so
        // diagrams look familiar. For custom themes we switch to "base" so
        // themeVariables actually drive the palette.
        var mermaidTheme;
        if (currentTheme === "custom") {
            mermaidTheme = "base";
        } else {
            mermaidTheme = themeType === "dark" ? "dark" : "default";
        }

        try {
            window.mermaid.initialize({
                startOnLoad: false,
                securityLevel: "strict",
                theme: mermaidTheme,
                suppressErrorRendering: true,
                maxTextSize: 50000,
                maxEdges: 500,
                fontFamily: bodyStyle.fontFamily,
                themeVariables: themeVariables,
            });
            mermaidReady = true;
        } catch (e) {
            logToHost("mermaid.initialize failed: " + (e && e.message ? e.message : e));
        }
    }

    function renderMermaidBlocks() {
        if (!contentEl) return Promise.resolve();
        // Even if mermaid is not loaded (e.g. jsdom smoke tests), still bind
        // the zoom hint badge so structure-level tests can verify the wiring.
        if (!window.mermaid) {
            bindZoomToMermaidWraps();
            return Promise.resolve();
        }
        var blocks = contentEl.querySelectorAll("pre.mermaid");
        if (blocks.length === 0) {
            bindZoomToMermaidWraps();
            return Promise.resolve();
        }
        try {
            var p = window.mermaid.run({
                querySelector: "#content pre.mermaid",
                suppressErrors: true,
            });
            // mermaid.run() returns a Promise; normalize after it resolves so
            // each rendered SVG carries explicit pixel width/height attributes
            // (Chromium's CSS `zoom` does not scale inline SVG sized with
            // `width="100%"` + `style="max-width: NNNpx"`, which is mermaid v11's
            // default output). The synchronous try/catch above only catches
            // setup-time throws, not async render failures, so attach a .catch
            // to log them without leaving an unhandled rejection. Once the
            // SVGs exist (success OR failure), wire up the zoom hint/click.
            if (p && typeof p.then === "function") {
                return p
                    .then(function () { normalizeMermaidSvgSizes(contentEl); })
                    .catch(function (e) {
                        logToHost("mermaid.run rejected: " + (e && e.message ? e.message : e));
                    })
                    .then(function () { bindZoomToMermaidWraps(); });
            }
            normalizeMermaidSvgSizes(contentEl);
            bindZoomToMermaidWraps();
            return Promise.resolve();
        } catch (e) {
            logToHost("mermaid.run failed: " + (e && e.message ? e.message : e));
            bindZoomToMermaidWraps();
            return Promise.resolve();
        }
    }

    // Rewrite each freshly rendered mermaid SVG so CSS `zoom` (Chromium) scales
    // it together with the surrounding markdown. Mermaid v11 emits
    //   <svg width="100%" style="max-width: NNNpx" viewBox="X Y W H">
    // which Chromium's `zoom` leaves at its intrinsic size. Replacing the
    // percentage width with explicit pixel attributes makes the SVG behave like
    // a replaced element with intrinsic dimensions, which `zoom` handles
    // correctly. We deliberately do NOT pair this with a CSS `max-width: 100%`
    // on the SVG — capping the width would proportionally shrink the entire SVG
    // (including in-diagram text driven by `themeVariables.fontSize`) whenever
    // the wrap is narrower than the diagram's natural width, breaking the font
    // sync with the surrounding prose. Horizontal overflow is handled by the
    // parent `.skim-mermaid-scroll { overflow-x: auto }` instead, mirroring the
    // upstream macOS SkimDown approach of leaving SVG at intrinsic 1:1 size.
    function normalizeMermaidSvgSizes(root) {
        if (!root || typeof root.querySelectorAll !== "function") return;
        var svgs = root.querySelectorAll(".skim-mermaid-wrap svg");
        for (var i = 0; i < svgs.length; i++) {
            var svg = svgs[i];
            // Already normalized (e.g., re-entry from theme refresh after this
            // SVG has been touched) — skip to avoid recomputing from a width
            // attribute we already overwrote.
            if (svg.getAttribute("data-skim-size-normalized") === "true") continue;

            var naturalWidth = readMermaidNaturalWidth(svg);
            var aspect = readSvgViewBoxAspect(svg);
            // Fall back to viewBox width when style.maxWidth is absent (e.g.,
            // diagrams rendered with `useMaxWidth: false`, or future mermaid
            // output variations). Skip silently if neither source is usable.
            if (!isFinitePositive(naturalWidth) && aspect.width > 0) {
                naturalWidth = aspect.width;
            }
            if (!isFinitePositive(naturalWidth)) continue;

            var naturalHeight = aspect.ratio > 0 ? naturalWidth * aspect.ratio : null;
            svg.setAttribute("width", String(naturalWidth));
            if (naturalHeight !== null && isFinitePositive(naturalHeight)) {
                svg.setAttribute("height", String(naturalHeight));
            }
            // Drop mermaid's `max-width: NNNpx` inline cap — `width` attribute
            // now fixes the natural size. Horizontal overflow is delegated to
            // the parent `.skim-mermaid-scroll { overflow-x: auto }` so the
            // SVG stays at 1:1 (keeping in-diagram text matched to body font).
            try { svg.style.removeProperty("max-width"); } catch (e) { /* best-effort */ }
            svg.setAttribute("data-skim-size-normalized", "true");
        }
    }

    function readMermaidNaturalWidth(svg) {
        // Mermaid sets style="max-width: NNNpx" when useMaxWidth: true.
        var styleAttr = svg.getAttribute("style") || "";
        var m = styleAttr.match(/max-width\s*:\s*([\d.]+)\s*px/i);
        if (!m) return NaN;
        var v = parseFloat(m[1]);
        return isFinitePositive(v) ? v : NaN;
    }

    function readSvgViewBoxAspect(svg) {
        var vb = svg.getAttribute("viewBox");
        if (!vb) return { width: 0, ratio: 0 };
        var parts = vb.trim().split(/[\s,]+/);
        if (parts.length < 4) return { width: 0, ratio: 0 };
        // viewBox = "min-x min-y width height"; min-x/min-y may legitimately be
        // negative, so only width (parts[2]) and height (parts[3]) must be
        // positive finite numbers.
        var w = parseFloat(parts[2]);
        var h = parseFloat(parts[3]);
        if (!isFinitePositive(w) || !isFinitePositive(h)) return { width: 0, ratio: 0 };
        return { width: w, ratio: h / w };
    }

    function isFinitePositive(n) {
        return typeof n === "number" && isFinite(n) && n > 0;
    }

    // ----- Mermaid zoom modal -----
    //
    // Click any rendered Mermaid diagram to open it in an overlay where the
    // user can pan (mouse drag / 1-finger touch) and zoom (Ctrl+wheel /
    // 2-finger pinch / toolbar / +/- keys). The modal is appended directly
    // under <body> — outside the #skim-zoom-root that owns the document zoom
    // — so the body-level CSS `zoom` does not double-scale this overlay.
    //
    // The modal DOM is created via createElement/textContent (never innerHTML)
    // so localized strings are never interpreted as markup.

    function ensureZoomModal() {
        if (zoomModal) return zoomModal;
        if (!document.body) return null;

        zoomModal = document.createElement("div");
        zoomModal.className = "skim-zoom-modal";
        zoomModal.setAttribute("role", "dialog");
        zoomModal.setAttribute("aria-modal", "true");

        zoomStage = document.createElement("div");
        zoomStage.className = "skim-zoom-stage";

        var toolbar = document.createElement("div");
        toolbar.className = "skim-zoom-toolbar";

        zoomBtnOut = document.createElement("button");
        zoomBtnOut.type = "button";
        zoomBtnOut.textContent = "\u2212"; // minus sign
        zoomBtnOut.addEventListener("click", function () { setZoomModalScale(zoomState.scale / 1.25); });

        zoomInfo = document.createElement("span");
        zoomInfo.className = "skim-zoom-info";
        zoomInfo.textContent = "100%";

        zoomBtnIn = document.createElement("button");
        zoomBtnIn.type = "button";
        zoomBtnIn.textContent = "+";
        zoomBtnIn.addEventListener("click", function () { setZoomModalScale(zoomState.scale * 1.25); });

        zoomBtnReset = document.createElement("button");
        zoomBtnReset.type = "button";
        zoomBtnReset.textContent = "\u21BB"; // clockwise arrow
        zoomBtnReset.addEventListener("click", function () { fitZoomModalToStage(); });

        zoomBtnClose = document.createElement("button");
        zoomBtnClose.type = "button";
        zoomBtnClose.textContent = "\u2715"; // multiplication X
        zoomBtnClose.addEventListener("click", closeZoomModal);

        toolbar.appendChild(zoomBtnOut);
        toolbar.appendChild(zoomInfo);
        toolbar.appendChild(zoomBtnIn);
        toolbar.appendChild(zoomBtnReset);
        toolbar.appendChild(zoomBtnClose);

        zoomContent = document.createElement("div");
        zoomContent.className = "skim-zoom-content";

        zoomHintEl = document.createElement("div");
        zoomHintEl.className = "skim-zoom-hint";

        zoomStage.appendChild(toolbar);
        zoomStage.appendChild(zoomContent);
        zoomStage.appendChild(zoomHintEl);
        zoomModal.appendChild(zoomStage);

        // Click on the backdrop (outside the stage) closes.
        zoomModal.addEventListener("click", function (ev) {
            if (ev.target === zoomModal) closeZoomModal();
        });

        // Pointer-events wiring lives on the stage so toolbar clicks are not
        // interpreted as pan starts.
        zoomStage.addEventListener("pointerdown", onZoomPointerDown);
        zoomStage.addEventListener("pointermove", onZoomPointerMove);
        zoomStage.addEventListener("pointerup", onZoomPointerEnd);
        zoomStage.addEventListener("pointercancel", onZoomPointerEnd);
        zoomStage.addEventListener("lostpointercapture", onZoomPointerEnd);

        // Stage-level wheel zoom. capture: false + passive: false so the
        // global handleWheelZoom early-returns when the event target is
        // inside the modal and we get the wheel here.
        zoomStage.addEventListener("wheel", onZoomWheel, { passive: false });

        // Forward Mermaid `<a>` clicks inside the cloned SVG to the host's
        // link pipeline (external -> default browser, relative -> open .md).
        zoomStage.addEventListener("click", onZoomStageClick);

        document.body.appendChild(zoomModal);
        applyStringsToZoomModal();
        return zoomModal;
    }

    function applyStringsToZoomModal() {
        if (!zoomModal) return;
        zoomModal.setAttribute("aria-label", t("mermaidZoom.dialogLabel"));
        if (zoomBtnIn)    zoomBtnIn.setAttribute("aria-label", t("mermaidZoom.zoomIn"));
        if (zoomBtnIn)    zoomBtnIn.setAttribute("title", t("mermaidZoom.zoomIn"));
        if (zoomBtnOut)   zoomBtnOut.setAttribute("aria-label", t("mermaidZoom.zoomOut"));
        if (zoomBtnOut)   zoomBtnOut.setAttribute("title", t("mermaidZoom.zoomOut"));
        if (zoomBtnReset) zoomBtnReset.setAttribute("aria-label", t("mermaidZoom.reset"));
        if (zoomBtnReset) zoomBtnReset.setAttribute("title", t("mermaidZoom.reset"));
        if (zoomBtnClose) zoomBtnClose.setAttribute("aria-label", t("mermaidZoom.close"));
        if (zoomBtnClose) zoomBtnClose.setAttribute("title", t("mermaidZoom.close"));
        if (zoomHintEl)   zoomHintEl.textContent = t("mermaidZoom.hint");
    }

    function applyStringsToZoomHints() {
        if (!contentEl) return;
        var hints = contentEl.querySelectorAll(".skim-mermaid-zoom-hint");
        for (var i = 0; i < hints.length; i++) {
            hints[i].textContent = t("mermaidZoom.openHint");
        }
        // Already-bound wraps also keep the same string as their accessible
        // name. New wraps pick it up on bind via t() at construction time.
        var boundWraps = contentEl.querySelectorAll(".skim-mermaid-wrap[data-zoom-bound='1']");
        for (var j = 0; j < boundWraps.length; j++) {
            boundWraps[j].setAttribute("aria-label", t("mermaidZoom.openHint"));
        }
    }

    // ----- Table of Contents -----
    //
    // Upstream macOS SkimDown exposes the rendered heading list from renderer.js
    // and shows it in a native AppKit pane. The Windows port keeps the same DOM
    // source of truth but renders the pane inside WebView2, so anchor scrolling
    // and active-heading tracking stay local to the document.

    function headingElements(root) {
        if (!root || typeof root.querySelectorAll !== "function") return [];
        return Array.prototype.slice.call(root.querySelectorAll("h1, h2, h3, h4, h5, h6"))
            .filter(function (heading) { return !!heading.id; });
    }

    function tableOfContents() {
        return headingElements(contentEl).map(function (heading) {
            var level = parseInt((heading.tagName || "H1").substring(1), 10);
            if (!isFinite(level) || level < 1 || level > 6) level = 1;
            return {
                level: level,
                title: (heading.textContent || "").trim(),
                id: heading.id,
            };
        });
    }

    function applyStringsToTableOfContents() {
        if (tocTitleEl) tocTitleEl.textContent = t("tableOfContents.title");
        if (tocEmptyEl) tocEmptyEl.textContent = t("tableOfContents.empty");
        if (tocOpenerEl) {
            var title = t("tableOfContents.title");
            tocOpenerEl.textContent = title;
            tocOpenerEl.title = title;
            tocOpenerEl.setAttribute("aria-label", title);
        }
    }

    function setTableOfContentsVisible(visible) {
        tableOfContentsVisible = !!visible;
        if (!tableOfContentsVisible) {
            setTableOfContentsDrawerOpen(false);
        }
        updateTableOfContentsVisibility();
    }

    function updateTableOfContentsVisibility() {
        if (!tocEl) return;
        var visible = tableOfContentsVisible && hasRenderedDocument && !isZoomModalOpen();
        tocEl.hidden = !visible;
        if (tocOpenerEl) {
            tocOpenerEl.hidden = !visible;
        }
        if (!visible) {
            tocDrawerOpen = false;
        }
        if (visible) {
            document.body.dataset.tocVisible = "true";
        } else {
            delete document.body.dataset.tocVisible;
        }
        if (tocDrawerOpen) {
            document.body.dataset.tocDrawerOpen = "true";
        } else {
            delete document.body.dataset.tocDrawerOpen;
        }
        if (tocOpenerEl) {
            tocOpenerEl.setAttribute("aria-expanded", tocDrawerOpen ? "true" : "false");
        }
    }

    function setTableOfContentsDrawerOpen(open) {
        tocDrawerOpen = !!open && tableOfContentsVisible && hasRenderedDocument && !isZoomModalOpen();
        updateTableOfContentsVisibility();
    }

    function toggleTableOfContentsDrawer() {
        setTableOfContentsDrawerOpen(!tocDrawerOpen);
    }

    function renderTableOfContents() {
        if (!tocEl || !tocListEl) return;

        currentTocItems = tableOfContents();
        activeHeadingID = "";
        tocListEl.textContent = "";

        var minLevel = 1;
        if (currentTocItems.length > 0) {
            minLevel = currentTocItems.reduce(function (min, item) {
                return Math.min(min, item.level);
            }, currentTocItems[0].level);
        }

        for (var i = 0; i < currentTocItems.length; i++) {
            var item = currentTocItems[i];
            var button = document.createElement("button");
            button.type = "button";
            button.className = "skim-toc-item";
            button.textContent = item.title || item.id;
            button.title = item.title || item.id;
            button.dataset.headingId = item.id;
            button.style.paddingLeft = (8 + Math.max(0, item.level - minLevel) * 12) + "px";
            button.addEventListener("click", function (ev) {
                ev.preventDefault();
                ev.stopPropagation();
                var id = ev.currentTarget && ev.currentTarget.dataset
                    ? ev.currentTarget.dataset.headingId
                    : "";
                if (!id) return;
                setActiveTableOfContentsHeading(id, false);
                scrollToAnchorByHash("#" + encodeURIComponent(id));
                setTableOfContentsDrawerOpen(false);
            });
            tocListEl.appendChild(button);
        }

        if (tocEmptyEl) tocEmptyEl.hidden = currentTocItems.length !== 0;
        tocListEl.hidden = currentTocItems.length === 0;
        applyStringsToTableOfContents();
        updateTableOfContentsVisibility();
        scheduleActiveHeadingUpdate();
    }

    function setActiveTableOfContentsHeading(headingID, scrollIntoView) {
        activeHeadingID = headingID || "";
        if (!tocListEl) return;
        var activeButton = null;
        var buttons = tocListEl.querySelectorAll(".skim-toc-item");
        for (var i = 0; i < buttons.length; i++) {
            var button = buttons[i];
            var isActive = !!activeHeadingID && button.dataset.headingId === activeHeadingID;
            button.classList.toggle("active", isActive);
            if (isActive) activeButton = button;
        }
        if (scrollIntoView && activeButton && activeButton.scrollIntoView) {
            try { activeButton.scrollIntoView({ block: "nearest" }); }
            catch (e) { /* best-effort */ }
        }
    }

    function scheduleActiveHeadingUpdate() {
        if (activeHeadingFrameRequest) return;
        var raf = window.requestAnimationFrame || function (cb) { return setTimeout(cb, 0); };
        activeHeadingFrameRequest = raf(function () {
            activeHeadingFrameRequest = 0;
            updateActiveHeading();
        });
    }

    function updateActiveHeading() {
        if (!hasRenderedDocument || currentTocItems.length === 0) {
            setActiveTableOfContentsHeading("", false);
            return;
        }

        var headings = headingElements(contentEl);
        if (headings.length === 0) {
            setActiveTableOfContentsHeading("", false);
            return;
        }

        var threshold = Math.min(160, Math.max(64, (window.innerHeight || 0) * 0.25));
        var active = headings[0];
        for (var i = 0; i < headings.length; i++) {
            var rect = headings[i].getBoundingClientRect();
            if (rect.top <= threshold) {
                active = headings[i];
            } else {
                break;
            }
        }

        if (active && active.id !== activeHeadingID) {
            setActiveTableOfContentsHeading(active.id, true);
        }
    }

    // Attach a click-to-zoom hint + click handler to every Mermaid wrap that
    // does not yet have one. Idempotent via dataset.zoomBound.
    function bindZoomToMermaidWraps() {
        if (!contentEl) return;
        var wraps = contentEl.querySelectorAll(".skim-mermaid-wrap");
        for (var i = 0; i < wraps.length; i++) {
            var wrap = wraps[i];
            if (wrap.dataset.zoomBound === "1") continue;
            wrap.dataset.zoomBound = "1";

            // Mark the wrap as a click target for accessibility tools.
            wrap.setAttribute("role", "button");
            wrap.setAttribute("tabindex", "0");
            wrap.setAttribute("aria-label", t("mermaidZoom.openHint"));

            // The hint badge has to be re-added every time we rebind, because
            // theme refresh wipes inner DOM (see refreshMermaidForTheme).
            // Ensure idempotency by removing any stray previous hint first.
            var existingHint = wrap.querySelector(":scope > .skim-mermaid-zoom-hint");
            if (existingHint) existingHint.remove();

            var hint = document.createElement("div");
            hint.className = "skim-mermaid-zoom-hint";
            hint.setAttribute("aria-hidden", "true");
            hint.textContent = t("mermaidZoom.openHint");
            wrap.appendChild(hint);

            // Event listeners are wired once for the lifetime of the wrap
            // node so theme refresh (which re-runs bind to restore the hint)
            // does not leak duplicate handlers. `dataset.zoomListener` is the
            // permanent marker; `dataset.zoomBound` may be cleared and reset
            // to trigger a hint rebuild.
            if (wrap.dataset.zoomListener !== "1") {
                wrap.dataset.zoomListener = "1";
                wrap.addEventListener("click", onMermaidWrapClick);
                wrap.addEventListener("keydown", onMermaidWrapKey);
            }
        }
    }

    function onMermaidWrapKey(ev) {
        // Enter / Space activate the wrap exactly like a real <button>.
        if (ev.key === "Enter" || ev.key === " ") {
            ev.preventDefault();
            onMermaidWrapClick({
                currentTarget: ev.currentTarget,
                target: ev.target,
            });
        }
    }

    function onMermaidWrapClick(ev) {
        // Do not open the modal when the user clicked a Mermaid-internal
        // hyperlink (Mermaid emits `<a xlink:href="...">` and similar for
        // `click NODE href "URL"` syntax). Let the existing #content click
        // delegate route the link to the host.
        if (ev.target && typeof ev.target.closest === "function") {
            if (ev.target.closest("a")) return;
            // Toolbar / copy buttons should never count as a zoom trigger.
            if (ev.target.closest(".skim-code-copy")) return;
        }
        // Skip if the user is currently selecting text.
        try {
            var sel = window.getSelection();
            if (sel && sel.toString && sel.toString().length > 0) return;
        } catch (e) { /* defensive */ }

        var wrap = ev.currentTarget;
        if (!wrap) return;
        var svg = wrap.querySelector("svg");
        if (!svg) return;
        openZoomModal(svg);
    }

    function openZoomModal(svgEl) {
        if (!svgEl) return;
        var modal = ensureZoomModal();
        if (!modal) return;

        // Remember focus so we can restore it on close.
        try { zoomLastFocused = document.activeElement; } catch (e) { zoomLastFocused = null; }

        // Clone the SVG so the original keeps rendering inline. Strip mermaid's
        // max-width cap so the modal can scale freely.
        var clone = svgEl.cloneNode(true);
        clone.removeAttribute("style");
        clone.style.maxWidth = "none";
        clone.style.maxHeight = "none";
        clone.style.display = "block";

        var dim = computeSvgNaturalSize(clone, svgEl);
        clone.setAttribute("width", String(dim.width));
        clone.setAttribute("height", String(dim.height));

        zoomContent.innerHTML = "";
        zoomContent.style.width  = dim.width  + "px";
        zoomContent.style.height = dim.height + "px";
        zoomContent.appendChild(clone);

        modal.classList.add("open");
        updateTableOfContentsVisibility();
        document.body.style.overflow = "hidden";

        // Take the markdown body OUT of the accessibility tree while the
        // modal owns the screen. `inert` (where available) also blocks
        // focus + pointer events on background content; aria-hidden is the
        // wider-support fallback for AT.
        var zoomRoot = document.getElementById("skim-zoom-root");
        if (zoomRoot) {
            zoomRoot.setAttribute("aria-hidden", "true");
            try { zoomRoot.inert = true; } catch (e) { /* older WebView2 */ }
        }

        // Compute fit-to-screen after the stage has its actual size.
        var raf = window.requestAnimationFrame || function (cb) { return setTimeout(cb, 0); };
        raf(function () { fitZoomModalToStage(); });

        // Focus the close button so keyboard users land somewhere meaningful.
        try { zoomBtnClose.focus(); } catch (e) { /* best-effort */ }
    }

    function computeSvgNaturalSize(clone, sourceSvg) {
        // Prefer the post-normalize explicit pixel size (set by
        // normalizeMermaidSvgSizes for the live SVG). If those aren't
        // available, fall back to viewBox dimensions; finally, fall back to
        // the rendered bounding rect.
        var w = 0, h = 0;
        if (sourceSvg) {
            var aw = parseFloat(sourceSvg.getAttribute("width"));
            var ah = parseFloat(sourceSvg.getAttribute("height"));
            if (isFinitePositive(aw) && isFinitePositive(ah)) {
                w = aw; h = ah;
            }
        }
        if (!w || !h) {
            var vb = clone.getAttribute("viewBox");
            if (vb) {
                var p = vb.split(/[\s,]+/).map(parseFloat);
                if (p.length === 4 && isFinitePositive(p[2]) && isFinitePositive(p[3])) {
                    w = p[2]; h = p[3];
                }
            }
        }
        if (!w || !h) {
            try {
                var rect = sourceSvg.getBoundingClientRect();
                w = rect.width || 800;
                h = rect.height || 600;
            } catch (e) {
                w = 800; h = 600;
            }
        }
        return { width: w, height: h };
    }

    function closeZoomModal() {
        if (!zoomModal) return;
        zoomModal.classList.remove("open");
        updateTableOfContentsVisibility();
        if (zoomContent) zoomContent.innerHTML = "";
        document.body.style.overflow = "";

        // Restore the markdown body to the accessibility tree.
        var zoomRoot = document.getElementById("skim-zoom-root");
        if (zoomRoot) {
            zoomRoot.removeAttribute("aria-hidden");
            try { zoomRoot.inert = false; } catch (e) { /* older WebView2 */ }
        }

        // Reset transform + pointer / pinch state.
        zoomState.scale = 1;
        zoomState.tx = 0;
        zoomState.ty = 0;
        zoomPointers.clear();
        zoomPinch = null;
        if (zoomStage) zoomStage.classList.remove("dragging");

        // Restore focus to whatever had it before the modal opened.
        try {
            if (zoomLastFocused && typeof zoomLastFocused.focus === "function") {
                zoomLastFocused.focus();
            }
        } catch (e) { /* best-effort */ }
        zoomLastFocused = null;
    }

    function applyZoomModalTransform() {
        if (!zoomContent || !zoomInfo) return;
        zoomContent.style.transform =
            "translate(calc(-50% + " + zoomState.tx + "px), calc(-50% + " + zoomState.ty + "px)) scale(" + zoomState.scale + ")";
        zoomInfo.textContent = Math.round(zoomState.scale * 100) + "%";
    }

    function setZoomModalScale(next) {
        if (!isFinite(next)) return;
        zoomState.scale = Math.min(ZOOM_MODAL_MAX, Math.max(ZOOM_MODAL_MIN, next));
        applyZoomModalTransform();
    }

    function fitZoomModalToStage() {
        if (!zoomStage || !zoomContent) return;
        var w = parseFloat(zoomContent.style.width) || 0;
        var h = parseFloat(zoomContent.style.height) || 0;
        if (w <= 0 || h <= 0) {
            zoomState.scale = 1;
            zoomState.tx = 0;
            zoomState.ty = 0;
            applyZoomModalTransform();
            return;
        }
        var rect = zoomStage.getBoundingClientRect();
        var padding = 40;
        var availW = (rect.width || 0) - padding;
        var availH = (rect.height || 0) - padding;
        var fit = 1;
        if (availW > 0 && availH > 0) {
            fit = Math.min(availW / w, availH / h);
        }
        zoomState.scale = Math.min(4, Math.max(ZOOM_MODAL_MIN, fit > 0 ? fit : 1));
        zoomState.tx = 0;
        zoomState.ty = 0;
        applyZoomModalTransform();
    }

    function onZoomWheel(ev) {
        ev.preventDefault();
        var factor = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
        setZoomModalScale(zoomState.scale * factor);
    }

    function onZoomPointerDown(ev) {
        // Toolbar buttons own their clicks — never start a pan there.
        if (ev.target && typeof ev.target.closest === "function" &&
            ev.target.closest(".skim-zoom-toolbar")) {
            return;
        }
        ev.preventDefault();
        try { zoomStage.setPointerCapture(ev.pointerId); } catch (e) { /* defensive */ }

        zoomPointers.set(ev.pointerId, {
            x: ev.clientX,
            y: ev.clientY,
            originX: ev.clientX,
            originY: ev.clientY,
            startTx: zoomState.tx,
            startTy: zoomState.ty,
        });

        if (zoomPointers.size === 1) {
            zoomStage.classList.add("dragging");
            zoomPinch = null;
        } else if (zoomPointers.size === 2) {
            // Bootstrap pinch state from the two active pointers.
            zoomPinch = computePinchSnapshot();
            zoomStage.classList.remove("dragging");
        }
    }

    function onZoomPointerMove(ev) {
        if (!zoomPointers.has(ev.pointerId)) return;
        var rec = zoomPointers.get(ev.pointerId);
        rec.x = ev.clientX;
        rec.y = ev.clientY;

        if (zoomPointers.size >= 2 && zoomPinch) {
            // Pinch: scale by distance ratio, pan by midpoint delta.
            var pts = Array.from(zoomPointers.values());
            var p0 = pts[0], p1 = pts[1];
            var dx = p1.x - p0.x, dy = p1.y - p0.y;
            var dist = Math.sqrt(dx * dx + dy * dy);
            if (dist > 0 && zoomPinch.startDist > 0) {
                var nextScale = zoomPinch.startScale * (dist / zoomPinch.startDist);
                nextScale = Math.min(ZOOM_MODAL_MAX, Math.max(ZOOM_MODAL_MIN, nextScale));
                zoomState.scale = nextScale;
            }
            var midX = (p0.x + p1.x) / 2;
            var midY = (p0.y + p1.y) / 2;
            zoomState.tx = zoomPinch.startTx + (midX - zoomPinch.startMidX);
            zoomState.ty = zoomPinch.startTy + (midY - zoomPinch.startMidY);
            applyZoomModalTransform();
        } else if (zoomPointers.size === 1) {
            // Single-pointer pan: delta is from the down position.
            zoomState.tx = rec.startTx + (rec.x - rec.originX);
            zoomState.ty = rec.startTy + (rec.y - rec.originY);
            applyZoomModalTransform();
        }
    }

    function computePinchSnapshot() {
        var pts = Array.from(zoomPointers.values());
        var p0 = pts[0], p1 = pts[1];
        var dx = p1.x - p0.x, dy = p1.y - p0.y;
        return {
            startDist: Math.sqrt(dx * dx + dy * dy),
            startScale: zoomState.scale,
            startMidX: (p0.x + p1.x) / 2,
            startMidY: (p0.y + p1.y) / 2,
            startTx: zoomState.tx,
            startTy: zoomState.ty,
        };
    }

    function onZoomPointerEnd(ev) {
        if (zoomPointers.has(ev.pointerId)) {
            zoomPointers.delete(ev.pointerId);
        }
        try { zoomStage.releasePointerCapture(ev.pointerId); } catch (e) { /* best-effort */ }

        if (zoomPointers.size < 2) {
            zoomPinch = null;
        }
        // When a pinch ends with one finger remaining, that pointer's
        // stored start coordinates and startTx/Ty still reflect the
        // pre-pinch drag baseline. Continuing to drag from there would
        // jump from the wrong reference. Reset the remaining pointer's
        // baseline to its current position + the new pan offset so the
        // very next move is a continuation, not a snap.
        if (zoomPointers.size === 1) {
            var remaining = zoomPointers.values().next().value;
            if (remaining) {
                remaining.originX = remaining.x;
                remaining.originY = remaining.y;
                remaining.startTx = zoomState.tx;
                remaining.startTy = zoomState.ty;
            }
        }
        if (zoomPointers.size === 0) {
            zoomStage.classList.remove("dragging");
        }
    }

    function onZoomStageClick(ev) {
        // SVG <a> elements: emulate the host link routing so external links
        // launch the default browser (not navigate the WebView itself).
        var target = ev.target;
        if (!target || typeof target.closest !== "function") return;
        var anchor = target.closest("a");
        if (!anchor) return;
        var href = anchor.getAttribute("href")
            || anchor.getAttributeNS("http://www.w3.org/1999/xlink", "href")
            || anchor.getAttribute("xlink:href");
        if (!href) return;
        ev.preventDefault();
        ev.stopPropagation();
        closeZoomModal();
        postToHost({
            type: "link",
            href: href,
            kind: isExternalUrl(href) ? "external" : "relative",
        });
    }

    // Tab / Shift+Tab focus trap among the 4 toolbar buttons.
    function trapZoomModalFocus(ev) {
        if (!isZoomModalOpen()) return;
        if (ev.key !== "Tab") return;
        var buttons = [zoomBtnOut, zoomBtnIn, zoomBtnReset, zoomBtnClose].filter(function (b) { return !!b; });
        if (buttons.length === 0) return;
        var active = document.activeElement;
        var idx = buttons.indexOf(active);
        if (idx === -1) {
            // Focus had escaped the modal; pull it back to the close button.
            ev.preventDefault();
            zoomBtnClose.focus();
            return;
        }
        var next = ev.shiftKey ? idx - 1 : idx + 1;
        if (next < 0) next = buttons.length - 1;
        else if (next >= buttons.length) next = 0;
        ev.preventDefault();
        buttons[next].focus();
    }

    // ----- GitHub Alerts (> [!NOTE], > [!TIP], > [!IMPORTANT], > [!WARNING], > [!CAUTION]) -----
    var ALERT_KINDS = {
        "NOTE":      { cls: "skim-alert-note",      icon: "\u2139\uFE0F",  label: "Note" },
        "TIP":       { cls: "skim-alert-tip",       icon: "\uD83D\uDCA1",  label: "Tip" },       // light bulb
        "IMPORTANT": { cls: "skim-alert-important", icon: "\uD83D\uDCE3",  label: "Important" }, // megaphone
        "WARNING":   { cls: "skim-alert-warning",   icon: "\u26A0\uFE0F",  label: "Warning" },
        "CAUTION":   { cls: "skim-alert-caution",   icon: "\uD83D\uDED1",  label: "Caution" },   // stop sign
    };
    var ALERT_RE = /^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(\r?\n|$)/;

    function applyGithubAlerts() {
        if (!contentEl) return;
        var blockquotes = contentEl.querySelectorAll("blockquote");
        blockquotes.forEach(function (bq) {
            // The marker must be the very first content of the blockquote, in
            // the first <p>. markdown-it emits `<blockquote>\n<p>[!NOTE]\n...`
            var firstP = bq.querySelector(":scope > p");
            if (!firstP) return;
            var firstNode = firstP.firstChild;
            if (!firstNode || firstNode.nodeType !== 3 /* TEXT_NODE */) return;
            var m = firstNode.nodeValue.match(ALERT_RE);
            if (!m) return;
            var kind = m[1];
            var info = ALERT_KINDS[kind];
            if (!info) return;

            // Strip the marker (and the trailing newline) from the text node.
            firstNode.nodeValue = firstNode.nodeValue.substring(m[0].length);
            // If the first <p> is now empty (no text, no children), drop it so
            // the alert title sits flush with the body content.
            if (firstNode.nodeValue.trim() === "" && !firstNode.nextSibling) {
                firstP.remove();
            } else if (firstNode.nodeValue.trim() === "" && firstNode.nextSibling) {
                // Drop the now-leading whitespace text node.
                firstP.removeChild(firstNode);
            }

            bq.classList.add("skim-alert", info.cls);

            var title = document.createElement("div");
            title.className = "skim-alert-title";
            var icon = document.createElement("span");
            icon.className = "skim-alert-icon";
            icon.setAttribute("aria-hidden", "true");
            icon.textContent = info.icon;
            var label = document.createElement("span");
            label.className = "skim-alert-label";
            label.textContent = info.label;
            title.appendChild(icon);
            title.appendChild(label);
            bq.insertBefore(title, bq.firstChild);
        });
    }

    // ----- Task list normalization -----
    // Convert leading "[ ]" / "[x]" inside <li> to a real (disabled) <input type="checkbox">.
    // markdown-it doesn't ship task-list rendering by default; this matches the
    // upstream macOS behavior.
    function normalizeTaskLists() {
        if (!contentEl) return;
        contentEl.querySelectorAll("li").forEach(function (item) {
            // Skip already-converted task items (idempotent).
            if (item.classList.contains("task-list-item")) return;
            // Find the first text-bearing leaf so we tolerate the structure
            //   <li><p>[ ] text</p></li>  and  <li>[ ] text</li>
            var firstText = item.firstChild;
            if (firstText && firstText.nodeType === 1 /* ELEMENT */ && firstText.tagName === "P") {
                firstText = firstText.firstChild;
            }
            if (!firstText || firstText.nodeType !== 3 /* TEXT */) return;
            var m = firstText.nodeValue.match(/^\s*\[( |x|X)\]\s+/);
            if (!m) return;
            firstText.nodeValue = firstText.nodeValue.slice(m[0].length);
            var checkbox = document.createElement("input");
            checkbox.type = "checkbox";
            checkbox.disabled = true;
            var isChecked = m[1].toLowerCase() === "x";
            checkbox.checked = isChecked;
            if (isChecked) {
                // Also set the attribute so the state survives innerHTML
                // serialization (used by search re-render).
                checkbox.setAttribute("checked", "");
            }
            item.classList.add("task-list-item");
            // Insert checkbox before the first inline content. If the first
            // text now lives inside a <p>, insert before that <p>'s first
            // child so visual order is `[x] text`.
            var insertBeforeParent = item.firstChild;
            if (insertBeforeParent && insertBeforeParent.nodeType === 1 && insertBeforeParent.tagName === "P") {
                insertBeforeParent.insertBefore(checkbox, insertBeforeParent.firstChild);
            } else {
                item.insertBefore(checkbox, item.firstChild);
            }
            // Tag the parent UL so we can suppress its bullet via CSS.
            if (item.parentElement && item.parentElement.tagName === "UL") {
                item.parentElement.classList.add("contains-task-list");
            }
        });
    }

    // ----- Heading anchor IDs -----
    // GitHub-style slug for headings; used both to assign id attributes and to
    // resolve in-document anchor links by slug.
    function slugifyHeadingText(text) {
        return String(text)
            .trim()
            .toLowerCase()
            .replace(/[^\p{Letter}\p{Mark}\p{Number}\s_-]+/gu, "")
            .replace(/\s+/g, "-")
            .replace(/-+/g, "-")
            .replace(/^-+|-+$/g, "");
    }

    function assignHeadingAnchorIDs() {
        if (!contentEl) return;
        var usedIDs = new Set();
        contentEl.querySelectorAll("[id]").forEach(function (el) {
            var id = el.getAttribute("id");
            if (id) usedIDs.add(id);
        });
        contentEl.querySelectorAll("h1, h2, h3, h4, h5, h6").forEach(function (h) {
            if (h.getAttribute("id")) return;
            var base = slugifyHeadingText(h.textContent || "") || "section";
            var id = base;
            var n = 1;
            while (usedIDs.has(id)) { id = base + "-" + n; n++; }
            h.setAttribute("id", id);
            usedIDs.add(id);
        });
    }

    // ----- GitHub backtick math: $`...`$ -----
    // markdown-it renders $`...`$ as text"$" + <code>...</code> + text"$".
    // We find <code> nodes flanked by "$" on both sides and replace with KaTeX.
    function convertBacktickMath() {
        if (!contentEl || !window.katex) return;
        var matches = [];
        contentEl.querySelectorAll("code").forEach(function (code) {
            if (code.closest("pre")) return;
            var prev = code.previousSibling;
            var next = code.nextSibling;
            if (!prev || prev.nodeType !== 3) return;
            if (!next || next.nodeType !== 3) return;
            if (!prev.nodeValue.endsWith("$")) return;
            if (!next.nodeValue.startsWith("$")) return;
            matches.push({ code: code, prev: prev, next: next });
        });
        matches.forEach(function (m) {
            var latex = m.code.textContent;
            try {
                var span = document.createElement("span");
                window.katex.render(latex, span, { throwOnError: false, displayMode: false });
                m.prev.nodeValue = m.prev.nodeValue.slice(0, -1);
                m.next.nodeValue = m.next.nodeValue.slice(1);
                if (m.prev.nodeValue === "") m.prev.remove();
                if (m.next.nodeValue === "") m.next.remove();
                m.code.replaceWith(span);
            } catch (_) {
                // Leave DOM unchanged on failure.
            }
        });
    }

    // ----- Color swatches (#rgb, #rgba, #rrggbb, #rrggbbaa) -----
    // KaTeX, Mermaid, alert titles) and inserts a small color box right after
    // each valid CSS hex color literal.
    //
    // Run AFTER DOMPurify so the inline `style="background:#xxx"` survives
    // (DOMPurify hook only keeps `style` on `.katex` subtrees otherwise).
    var COLOR_RE = /#([0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{4}|[0-9a-fA-F]{3})\b/g;
    var COLOR_SKIP_SELECTOR =
        "code, pre, a, h1, h2, h3, h4, h5, h6, .katex, .katex-display, " +
        ".skim-mermaid-wrap, .skim-alert-title, .skim-color-swatch, svg";

    function applyColorSwatches() {
        if (!contentEl) return;
        var walker = document.createTreeWalker(contentEl, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (!node.nodeValue || node.nodeValue.indexOf("#") === -1) {
                    return NodeFilter.FILTER_REJECT;
                }
                if (node.parentNode && node.parentNode.closest &&
                    node.parentNode.closest(COLOR_SKIP_SELECTOR)) {
                    return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
            },
        });

        var targets = [];
        var n = walker.nextNode();
        while (n) {
            targets.push(n);
            n = walker.nextNode();
        }

        targets.forEach(function (textNode) {
            var value = textNode.nodeValue;
            COLOR_RE.lastIndex = 0;
            if (!COLOR_RE.test(value)) return;
            COLOR_RE.lastIndex = 0;

            var frag = document.createDocumentFragment();
            var lastIndex = 0;
            var match;
            while ((match = COLOR_RE.exec(value)) !== null) {
                if (match.index > lastIndex) {
                    frag.appendChild(document.createTextNode(value.substring(lastIndex, match.index)));
                }
                frag.appendChild(document.createTextNode(match[0]));
                var swatch = document.createElement("span");
                swatch.className = "skim-color-swatch";
                swatch.setAttribute("aria-hidden", "true");
                // Inline style is safe: match[0] is a regex-validated hex
                // color literal, so it cannot inject other CSS.
                swatch.style.background = match[0];
                frag.appendChild(swatch);
                lastIndex = match.index + match[0].length;
            }
            if (lastIndex < value.length) {
                frag.appendChild(document.createTextNode(value.substring(lastIndex)));
            }
            textNode.parentNode.replaceChild(frag, textNode);
        });
    }

    function refreshMermaidForTheme() {
        if (!window.mermaid || !contentEl) return;
        var blocks = contentEl.querySelectorAll("pre.mermaid, .skim-mermaid-wrap pre, .skim-mermaid-wrap svg");
        if (blocks.length === 0) return;
        // If the zoom modal is showing a clone of an SVG that's about to be
        // re-rendered under the new theme, close it so the user doesn't see
        // stale colors.
        if (isZoomModalOpen()) closeZoomModal();
        // Reset each Mermaid block back to its source so .run() will re-render under the new theme.
        var wraps = contentEl.querySelectorAll(".skim-mermaid-wrap");
        wraps.forEach(function (wrap) {
            var src = wrap.getAttribute("data-source");
            if (src == null) {
                var existing = wrap.querySelector("pre.mermaid");
                src = existing ? (existing.getAttribute("data-source") || existing.textContent) : "";
                wrap.setAttribute("data-source", src);
            }
            // Rebuild the inner DOM but preserve the wrap/scroll split. The
            // zoom-hint badge (if already bound) is also flushed; the
            // post-render bindZoomToMermaidWraps() call re-attaches it idempotently.
            wrap.innerHTML = '';
            wrap.removeAttribute('data-zoom-bound');
            var scroll = document.createElement("div");
            scroll.className = "skim-mermaid-scroll";
            var pre = document.createElement("pre");
            pre.className = "mermaid";
            pre.setAttribute("data-source", src);
            pre.textContent = src;
            scroll.appendChild(pre);
            wrap.appendChild(scroll);
        });
        renderMermaidBlocks();
    }

    // Apply CSS variables for a custom theme via inline style on document.body.
    // We set them on <body> (not <html>) because the dark/light fallback CSS rules
    // also target body[data-theme=...]; setting custom vars on <html> loses the
    // cascade race because body's selector-defined values would override the
    // html-level inline for any descendant element.
    function applyThemeVars(themeVars) {
        // Strip any previously applied custom vars so stale values don't leak
        // into the next theme.
        var bodyEl = document.body;
        for (var i = 0; i < appliedCustomVars.length; i++) {
            try { bodyEl.style.removeProperty(appliedCustomVars[i]); } catch (e) { /* best-effort */ }
        }
        appliedCustomVars = [];

        if (!themeVars || typeof themeVars !== "object") return;
        for (var name in themeVars) {
            if (!Object.prototype.hasOwnProperty.call(themeVars, name)) continue;
            if (typeof name !== "string" || name.indexOf("--skim-") !== 0) continue;
            var value = themeVars[name];
            if (typeof value !== "string" || value.length === 0) continue;
            try {
                bodyEl.style.setProperty(name, value);
                appliedCustomVars.push(name);
            } catch (e) {
                // ignore unsupported values
            }
        }
    }

    function setTheme(theme, themeType, themeIsDark, themeVars) {
        var t = (theme || "light").toString().toLowerCase();
        // Map "system" to its effective light/dark; for "custom" keep the literal
        // so CSS selectors body[data-theme="custom"][data-theme-type="..."] match.
        if (t !== "dark" && t !== "light" && t !== "custom") {
            t = "light";
        }
        currentTheme = t;

        // Resolve a concrete light/dark flag for code highlight + Mermaid.
        var resolvedType;
        if (typeof themeIsDark === "boolean") {
            resolvedType = themeIsDark ? "dark" : "light";
        } else if (themeType === "dark" || themeType === "light") {
            resolvedType = themeType;
        } else {
            resolvedType = (t === "dark") ? "dark" : "light";
        }
        currentThemeType = resolvedType;
        currentThemeIsDark = (resolvedType === "dark");

        document.body.dataset.theme = t;
        document.body.dataset.themeType = resolvedType;

        applyThemeVars(themeVars);

        var lightLink = document.getElementById("hljs-light");
        var darkLink  = document.getElementById("hljs-dark");
        if (lightLink && darkLink) {
            lightLink.disabled = (resolvedType === "dark");
            darkLink.disabled  = (resolvedType !== "dark");
        }
        if (window.mermaid) {
            initMermaid(resolvedType);
            refreshMermaidForTheme();
        }
    }

    function setZoom(factor) {
        applyZoomLocal(factor);
    }

    // Host-initiated zoom (menu / keyboard / startup restore from AppSettings).
    // Must cancel any pending debounced post so a stale gesture value doesn't
    // overwrite the authoritative host value right after we accept it.
    function setZoomFromHost(factor) {
        cancelZoomPost();
        applyZoomLocal(factor);
    }

    function applyZoomLocal(factor) {
        var f = parseFloat(factor);
        if (!isFinite(f) || f <= 0) return;
        f = clampZoom(f);
        currentZoom = f;
        // Apply zoom to the dedicated zoom-root wrapper (not body) so the
        // Mermaid zoom modal — which is appended directly under <body> — is
        // not double-scaled by the document zoom.
        var root = document.getElementById("skim-zoom-root");
        if (root) {
            root.style.zoom = String(f);
        } else {
            // Fallback for environments without the wrapper (e.g. tests with
            // a synthetic DOM).
            document.body.style.zoom = String(f);
        }
    }

    function clampZoom(f) {
        if (!isFinite(f)) return currentZoom;
        if (f < ZOOM_MIN) return ZOOM_MIN;
        if (f > ZOOM_MAX) return ZOOM_MAX;
        return f;
    }

    // Normalize wheel deltaY so the gesture coefficient is independent of the
    // event's deltaMode. Chromium/WebView2 almost always uses pixel mode on
    // Windows, but some drivers / accessibility tools emit line- or page-mode
    // wheel events; we approximate to pixels so the zoom feel stays consistent.
    function normalizeWheelDeltaY(ev) {
        var dy = ev.deltaY;
        if (ev.deltaMode === 1 /* DOM_DELTA_LINE */) {
            dy *= 16;
        } else if (ev.deltaMode === 2 /* DOM_DELTA_PAGE */) {
            dy *= (window.innerHeight || 800);
        }
        return dy;
    }

    // Multiplicative zoom step. Using exp keeps the gesture symmetric
    // (pinch-out then pinch-in by the same amount returns to the original
    // factor) and naturally smooth for both mouse wheel and trackpad pinch:
    //   * mouse wheel notch (deltaY ~ 100) -> ~9.5% change per notch
    //   * trackpad pinch (deltaY ~ 1..10)  -> ~0.1%..1% per tick (smooth)
    function applyZoomDelta(deltaY) {
        if (!isFinite(deltaY) || deltaY === 0) return;
        var next = clampZoom(currentZoom * Math.exp(-deltaY * 0.001));
        if (next === currentZoom) return;
        applyZoomLocal(next);
        scheduleZoomPost(next);
    }

    function scheduleZoomPost(factor) {
        zoomPendingFactor = factor;
        if (zoomPostTimer) {
            clearTimeout(zoomPostTimer);
        }
        zoomPostTimer = setTimeout(flushZoomPost, zoomPostDebounceMs);
    }

    function cancelZoomPost() {
        if (zoomPostTimer) {
            clearTimeout(zoomPostTimer);
            zoomPostTimer = 0;
        }
        zoomPendingFactor = null;
    }

    function flushZoomPost() {
        if (zoomPostTimer) {
            clearTimeout(zoomPostTimer);
            zoomPostTimer = 0;
        }
        if (zoomPendingFactor === null) return;
        var f = zoomPendingFactor;
        zoomPendingFactor = null;
        postToHost({ type: "zoomChanged", factor: f });
    }

    // Ctrl+Wheel and trackpad pinch (Chromium synthesizes wheel events with
    // ctrlKey=true for precision-touchpad pinch gestures on Windows). We
    // handle both with a single capture-phase, non-passive listener so we can
    // call preventDefault() before any inner element scrolls.
    function handleWheelZoom(ev) {
        if (!ev.ctrlKey) return;
        // Mermaid zoom modal owns wheel/pinch when open — do not also adjust
        // the document zoom in that case. The modal's own stage handler will
        // (re-)act on the wheel event.
        if (isZoomModalOpen() && zoomModal && zoomModal.contains(ev.target)) {
            return;
        }
        // Skip if the user is interacting with form fields; nothing in the
        // rendered Markdown should be editable, but be defensive.
        if (isEditableTarget(ev.target)) return;
        ev.preventDefault();
        ev.stopPropagation();
        applyZoomDelta(normalizeWheelDeltaY(ev));
    }

    function rewriteRelativeUrls(html) {
        if (!currentContentBaseUri) return html;
        // Use a DOM walker to rewrite image src values that look like relative paths.
        var tmp = document.createElement("div");
        tmp.innerHTML = html;

        function isAbsolute(u) {
            return /^([a-z][a-z0-9+.-]*:|\/\/|#)/i.test(u);
        }

        // Join a relative path onto a base URI (the host has already mapped
        // contentBaseUri to the source folder).
        function resolveOn(baseUri, sourceDir, relPath) {
            try {
                // sourceDir is a forward-slash relative dir under contentBaseUri.
                var basis = baseUri.endsWith("/") ? baseUri : baseUri + "/";
                if (sourceDir) basis += sourceDir.replace(/\/?$/, "/");
                return new URL(relPath, basis).toString();
            } catch (e) {
                return relPath;
            }
        }

        tmp.querySelectorAll("img[src]").forEach(function (el) {
            var src = el.getAttribute("src");
            if (!src || isAbsolute(src)) return;
            el.setAttribute("src", resolveOn(currentContentBaseUri, currentSourceDir, src));
        });

        return tmp.innerHTML;
    }

    // KaTeX emits MathML + a parallel HTML span tree under .katex root. Allow
    // both, but only when the element is inside a .katex tree (DOMPurify hook
    // below enforces the scope so raw <math> in arbitrary markdown is still
    // stripped).
    var KATEX_TAGS = [
        "math", "annotation", "semantics", "mtext", "mn", "mo", "mi", "mspace",
        "mover", "munder", "munderover", "msup", "msub", "msubsup", "mfrac",
        "mroot", "msqrt", "mtable", "mtr", "mtd", "mlabeledtr", "mrow", "menclose",
        "mstyle", "mpadded", "mphantom", "mglyph", "mfenced", "merror"
    ];
    var KATEX_ATTRS = [
        "accent", "accentunder", "align", "bevelled", "close", "columnsalign",
        "columnlines", "columnspan", "denomalign", "depth", "dir", "display",
        "displaystyle", "encoding", "fence", "frame", "height", "linethickness",
        "lspace", "lquote", "mathbackground", "mathcolor", "mathsize", "mathvariant",
        "maxsize", "minsize", "movablelimits", "notation", "numalign", "open",
        "rowalign", "rowlines", "rowspacing", "rowspan", "rspace", "rquote",
        "scriptlevel", "scriptminsize", "scriptsizemultiplier", "selection",
        "separator", "separators", "stretchy", "subscriptshift", "supscriptshift",
        "symmetric", "voffset", "width", "xmlns", "aria-hidden"
    ];

    function installPurifyHooks() {
        if (!window.DOMPurify) return;
        // Allow KaTeX-emitted inline style on elements that live inside a
        // .katex tree. Without this hook, DOMPurify with default config would
        // strip the inline `style` attribute KaTeX uses for character offsets.
        window.DOMPurify.addHook("uponSanitizeAttribute", function (node, data) {
            if (data.attrName === "style") {
                var el = node;
                while (el) {
                    if (el.classList && (el.classList.contains("katex") ||
                                          el.classList.contains("katex-display") ||
                                          el.classList.contains("katex-mathml") ||
                                          el.classList.contains("katex-html"))) {
                        return; // keep style
                    }
                    el = el.parentNode;
                }
                // Outside a KaTeX subtree: strip inline styles (default behavior).
                data.keepAttr = false;
            }
        });
    }

    function render(markdown, sourcePath, contentBaseUri, theme, themeType, themeIsDark, themeVars) {
        if (typeof markdown !== "string") markdown = "";
        // If the user switches to another file while the zoom modal is open,
        // the clone in the modal now points at a diagram that's about to be
        // detached. Close the modal so we never leave a stale view on top.
        if (isZoomModalOpen()) closeZoomModal();
        currentContentBaseUri = contentBaseUri || "";
        // sourceDir is the relative folder portion of sourcePath, forward-slash form.
        currentSourceDir = "";
        if (sourcePath && typeof sourcePath === "string") {
            var normalizedSourcePath = sourcePath.replace(/\\/g, "/");
            var idx = normalizedSourcePath.lastIndexOf("/");
            if (idx >= 0) currentSourceDir = normalizedSourcePath.substring(0, idx);
        }
        if (theme || themeType || typeof themeIsDark === "boolean" || themeVars) {
            setTheme(theme || currentTheme, themeType, themeIsDark, themeVars);
        }

        var rendererInstance = ensureMarkdown();
        var raw;
        try {
            raw = rendererInstance.render(markdown);
        } catch (e) {
            raw = '<div class="skim-error">Markdown render failed: ' + escapeHtml(String(e)) + '</div>';
        }

        raw = rewriteRelativeUrls(raw);

        var clean = window.DOMPurify
            ? window.DOMPurify.sanitize(raw, {
                  USE_PROFILES: { html: true, mathMl: true },
                  ADD_TAGS: KATEX_TAGS.concat(["button"]),
                  ADD_ATTR: KATEX_ATTRS.concat(["target", "rel", "id", "type", "aria-label", "data-source", "width", "height", "checked", "disabled"]),
                  ALLOW_DATA_ATTR: true,
                  FORBID_TAGS: ["style", "script", "iframe", "object", "embed", "form"],
                  FORBID_ATTR: ["onerror", "onload", "onclick"],
              })
            : raw;

        lastRenderedMarkdown = markdown;
        lastRenderedHtml = clean;
        contentEl.innerHTML = clean;
        hasRenderedDocument = true;

        // DOMPurify strips `data-source` on <pre class="mermaid"> when the
        // value contains characters it flags as risky (e.g. raw `>`), so
        // re-attach it from the post-sanitize textContent. Mermaid replaces
        // textContent with SVG once it runs, so theme-refresh later needs
        // this attribute to recover the original source.
        if (contentEl) {
            // Descendant selector (not `>`) because the wrap layout puts
            // <pre> inside `.skim-mermaid-scroll`:
            //   .skim-mermaid-wrap > .skim-mermaid-scroll > pre.mermaid
            contentEl.querySelectorAll(".skim-mermaid-wrap pre.mermaid").forEach(function (pre) {
                if (!pre.hasAttribute("data-source")) {
                    pre.setAttribute("data-source", pre.textContent || "");
                }
            });
        }

        // Post-sanitize DOM enhancements (run before Mermaid so search-walker
        // counts stay consistent with the final rendered tree):
        //  - Heading anchor IDs: assign slug-based id="" to <h1..h6> so
        //    internal `[link](#slug)` clicks can resolve.
        //  - Task list normalization: `- [ ]` / `- [x]` -> styled checkboxes.
        //  - GitHub Alerts: `> [!NOTE]` blockquotes become styled alert cards.
        //  - Backtick math: `$\`x\`$` -> inline KaTeX.
        //  - Color swatches: small color box appended after `#rrggbb` in prose.
        // Color swatches run AFTER DOMPurify because the swatch needs an inline
        // style attribute that our DOMPurify hook strips outside `.katex` subtrees.
        assignHeadingAnchorIDs();
        renderTableOfContents();
        normalizeTaskLists();
        applyGithubAlerts();
        convertBacktickMath();
        applyColorSwatches();

        // Mermaid renders AFTER sanitization (it injects SVG into the live DOM).
        // We pin securityLevel: "strict" and constrain inputs to limit the blast
        // radius even though the markdown is locally-opened content.
        renderMermaidBlocks();

        try { window.scrollTo(0, 0); } catch (e) { /* test envs may not implement scrollTo */ }
        scheduleActiveHeadingUpdate();

        // Re-apply current search if any.
        if (search.query) {
            applySearch(search.query, search.caseSensitive, /*resetIndex=*/ false);
        }
    }

    function escapeHtml(s) {
        return s.replace(/[&<>"']/g, function (c) {
            return ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c];
        });
    }

    // ----- Link click handling -----
    function isExternalUrl(href) {
        return /^https?:\/\//i.test(href);
    }

    function scrollToAnchorByHash(hash) {
        function scrollTopSmooth() {
            try { window.scrollTo({ top: 0, behavior: "smooth" }); }
            catch (e) { try { window.scrollTo(0, 0); } catch (_) { /* ignore */ } }
        }
        if (!hash) { scrollTopSmooth(); return true; }
        var raw = hash.charAt(0) === "#" ? hash.substring(1) : hash;
        if (!raw) { scrollTopSmooth(); return true; }
        var decoded = raw;
        try { decoded = decodeURIComponent(raw); } catch (e) { /* keep raw */ }
        var slug = slugifyHeadingText(decoded);
        var target = document.getElementById(decoded);
        if (!target && slug && slug !== decoded) {
            target = document.getElementById(slug);
        }
        if (!target && window.CSS && CSS.escape) {
            try {
                target = document.querySelector("[name='" + CSS.escape(decoded) + "']");
            } catch (e) { /* ignore */ }
        }
        if (target) {
            try { if (target.scrollIntoView) target.scrollIntoView({ block: "start", behavior: "smooth" }); }
            catch (e) { /* ignore — best effort */ }
            return true;
        }
        return false;
    }

    function handleClick(ev) {
        // Code-block copy button hits first (so anchor handling never sees them).
        var copyBtn = ev.target.closest && ev.target.closest(".skim-code-copy");
        if (copyBtn) {
            ev.preventDefault();
            ev.stopPropagation();
            handleCopyClick(copyBtn);
            return;
        }

        var anchor = ev.target.closest && ev.target.closest("a");
        if (!anchor) return;
        // Mermaid emits `<a xlink:href="URL">` for `click NODE href "URL"`
        // syntax, so an inline diagram link only has the namespaced form.
        // Try plain href first (HTML5 / most cases), then both forms of the
        // xlink attribute. Without the xlink fallback, in-diagram external
        // links would bypass IExternalUriLauncher and navigate inside the
        // WebView.
        var href = anchor.getAttribute("href")
            || anchor.getAttributeNS("http://www.w3.org/1999/xlink", "href")
            || anchor.getAttribute("xlink:href");
        if (!href) return;

        if (href.charAt(0) === "#") {
            // In-document anchor. Try slug-aware scroll for smoother handling
            // of headings without explicit ids, and notify host either way so
            // it can persist scroll state later if desired.
            ev.preventDefault();
            ev.stopPropagation();
            scrollToAnchorByHash(href);
            postToHost({ type: "link", href: href, kind: "anchor" });
            return;
        }

        ev.preventDefault();
        ev.stopPropagation();
        postToHost({
            type: "link",
            href: href,
            kind: isExternalUrl(href) ? "external" : "relative",
        });
    }

    function handleCopyClick(btn) {
        var wrap = btn.closest(".skim-code");
        if (!wrap) return;
        var codeEl = wrap.querySelector("pre code");
        var text = codeEl ? codeEl.innerText : "";
        if (!text) return;

        var label = btn.querySelector(".skim-code-copy-label");
        var originalText = label ? label.textContent : "";

        function flash(msg) {
            if (!label) return;
            label.textContent = msg;
            btn.classList.add("copied");
            setTimeout(function () {
                label.textContent = originalText || "Copy";
                btn.classList.remove("copied");
            }, 1000);
        }

        var done = false;
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(function () {
                    flash("Copied");
                }, function () {
                    // Fallback: ask host to copy via WinUI clipboard.
                    postToHost({ type: "copy", text: text });
                    flash("Copied");
                });
                done = true;
            }
        } catch (e) { /* fall through */ }
        if (!done) {
            postToHost({ type: "copy", text: text });
            flash("Copied");
        }
    }

    // ----- In-document search (JS-side, per critique) -----
    function clearHighlights() {
        // Replace every <mark.skim-search-hit> with its text node.
        var hits = contentEl.querySelectorAll("mark.skim-search-hit");
        hits.forEach(function (m) {
            var text = document.createTextNode(m.textContent);
            m.parentNode.replaceChild(text, m);
        });
        contentEl.normalize();
        search.hits = [];
        search.current = -1;
    }

    function applySearch(query, caseSensitive, resetIndex) {
        clearHighlights();
        search.query = query || "";
        search.caseSensitive = !!caseSensitive;

        if (!search.query) {
            postToHost({ type: "search/result", total: 0, current: 0 });
            return;
        }

        var flags = caseSensitive ? "g" : "gi";
        var safe = search.query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
        var re = new RegExp(safe, flags);

        var walker = document.createTreeWalker(contentEl, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (!node.nodeValue) return NodeFilter.FILTER_REJECT;
                // Skip text inside existing hits.
                if (node.parentNode && node.parentNode.closest && node.parentNode.closest("mark.skim-search-hit")) {
                    return NodeFilter.FILTER_REJECT;
                }
                // Skip text inside Mermaid diagrams and SVG (Mermaid renders to
                // SVG; injecting <mark> inside SVG would corrupt the diagram).
                // Also skip the KaTeX MathML branch (we keep the HTML branch
                // visible; KaTeX inserts duplicate text in both trees).
                // Skip color swatches (they have no text but a closest() check
                // is harmless and future-proof).
                if (node.parentNode && node.parentNode.closest) {
                    if (node.parentNode.closest(".skim-mermaid-wrap, svg, .katex-mathml, .skim-color-swatch")) {
                        return NodeFilter.FILTER_REJECT;
                    }
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        var textNodes = [];
        var node = walker.nextNode();
        while (node) {
            textNodes.push(node);
            node = walker.nextNode();
        }

        textNodes.forEach(function (textNode) {
            var value = textNode.nodeValue;
            re.lastIndex = 0;
            var match;
            var lastIndex = 0;
            var fragment = null;

            while ((match = re.exec(value)) !== null) {
                if (!fragment) fragment = document.createDocumentFragment();
                if (match.index > lastIndex) {
                    fragment.appendChild(document.createTextNode(value.substring(lastIndex, match.index)));
                }
                var mark = document.createElement("mark");
                mark.className = "skim-search-hit";
                mark.textContent = match[0];
                fragment.appendChild(mark);
                search.hits.push(mark);
                lastIndex = match.index + match[0].length;
                if (match[0].length === 0) re.lastIndex++;
            }

            if (fragment) {
                if (lastIndex < value.length) {
                    fragment.appendChild(document.createTextNode(value.substring(lastIndex)));
                }
                textNode.parentNode.replaceChild(fragment, textNode);
            }
        });

        if (resetIndex !== false && search.hits.length > 0) {
            setCurrent(0);
        }

        postToHost({
            type: "search/result",
            total: search.hits.length,
            current: search.hits.length > 0 ? search.current + 1 : 0,
        });
    }

    function setCurrent(index) {
        if (search.hits.length === 0) {
            search.current = -1;
            return;
        }
        if (search.current >= 0 && search.current < search.hits.length) {
            search.hits[search.current].classList.remove("current");
        }
        var n = search.hits.length;
        search.current = ((index % n) + n) % n;
        var el = search.hits[search.current];
        el.classList.add("current");
        try { if (el.scrollIntoView) el.scrollIntoView({ block: "center", behavior: "auto" }); }
        catch (e) { /* environments without scrollIntoView (e.g. tests) — ignore */ }
    }

    function searchNext() {
        if (search.hits.length === 0) return;
        setCurrent(search.current + 1);
        postToHost({ type: "search/result", total: search.hits.length, current: search.current + 1 });
    }

    function searchPrev() {
        if (search.hits.length === 0) return;
        setCurrent(search.current - 1);
        postToHost({ type: "search/result", total: search.hits.length, current: search.current + 1 });
    }

    function searchClear() {
        clearHighlights();
        search.query = "";
        postToHost({ type: "search/result", total: 0, current: 0 });
    }

    // ----- Keyboard accelerator forwarding -----
    //
    // When the WebView2's child HWND has focus, WinUI's KeyboardAccelerator
    // on the menu items never fires — the keys are consumed by the browser
    // before they reach the WinUI input pipeline. To make the menu shortcuts
    // (Ctrl+F, Ctrl+B, Ctrl+O, Ctrl++ / - / 0, etc.) work uniformly, we
    // detect the relevant combos here and post `{type:"shortcut", id}` back
    // to the host so it can invoke the same handler the menu would.
    //
    // We intentionally do NOT forward Ctrl+C: native browser copy is faster
    // and writes the user's actual selection to the clipboard directly. The
    // menu's Ctrl+C is the fallback for when focus is in WinUI.
    function shortcutIdFromEvent(ev) {
        // Skip Alt / Meta combos — none of our shortcuts use them, and Alt+
        // is widely used for menu mnemonics / system shortcuts.
        if (!ev.ctrlKey || ev.altKey || ev.metaKey) return null;

        var key = ev.key || "";

        // Symbol keys are layout-stable across letter/digit cases, so match
        // them BEFORE the lowercase fold (Shift+= produces "+", not "=").
        // ";" is included as a zoom-in alias for Japanese (JIS) keyboards,
        // where the "+" character lives on Shift+";" — so Ctrl+; (no Shift)
        // gives JIS users the same one-handed feel US users get from Ctrl+=.
        if (key === "+" || key === "=" || key === ";") return "zoom-in";
        if (key === "-") return "zoom-out";
        if (key === "0" && !ev.shiftKey) return "zoom-reset";
        // Content max-width step (Ctrl+] / Ctrl+[).
        // Use the produced character ("]" / "[") so JIS keyboards — which
        // generate these via Shift on different physical keys — still hit
        // here. Host-side OnPageKeyDown also has a VK_OEM_4/VK_OEM_6 path
        // for the WinUI-focused case (sidebar tree, etc.).
        if (key === "]") return "content-width-wider";
        if (key === "[") return "content-width-narrower";

        var lk = key.toLowerCase();

        if (ev.shiftKey) {
            // Ctrl+Shift+G is Find Previous; no other Ctrl+Shift+letter
            // combos are mapped today.
            if (lk === "g") return "find-prev";
            return null;
        }

        switch (lk) {
            case "o": return "open-folder";
            case "w": return "close-window";
            case "n": return "new-window";
            case "f": return "find";
            case "g": return "find-next";
            case "e": return "use-selection-for-find";
            case "b": return "toggle-sidebar";
            case "a": return "select-all";
            case "m": return "minimize";
            default:  return null;
        }
    }

    function isEditableTarget(t) {
        if (!t) return false;
        try {
            if (t.isContentEditable) return true;
            var tag = (t.tagName || "").toUpperCase();
            if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
        } catch (e) { /* defensive */ }
        return false;
    }

    function handleAcceleratorKey(ev) {
        // When the Mermaid zoom modal is open, intercept its keyboard
        // shortcuts (Esc / +/- / 0 / Tab) BEFORE the host-shortcut path so
        // they don't double-fire (e.g. "0" would normally be ignored by
        // shortcutIdFromEvent because it requires Ctrl, but the modal owns
        // these plain keys).
        if (isZoomModalOpen()) {
            // Tab focus trap.
            if (ev.key === "Tab") {
                trapZoomModalFocus(ev);
                return;
            }
            if (ev.key === "Escape") {
                ev.preventDefault();
                ev.stopPropagation();
                closeZoomModal();
                return;
            }
            // Plain +/- and Ctrl+/- both adjust the modal scale while open.
            if (!isEditableTarget(ev.target)) {
                if (ev.key === "+" || ev.key === "=") {
                    ev.preventDefault();
                    ev.stopPropagation();
                    setZoomModalScale(zoomState.scale * 1.25);
                    return;
                }
                if (ev.key === "-" || ev.key === "_") {
                    ev.preventDefault();
                    ev.stopPropagation();
                    setZoomModalScale(zoomState.scale / 1.25);
                    return;
                }
                if (ev.key === "0") {
                    ev.preventDefault();
                    ev.stopPropagation();
                    fitZoomModalToStage();
                    return;
                }
            }
            // Other keys (e.g. Ctrl+W to close window) still fall through.
        }

        if (tocDrawerOpen && ev.key === "Escape") {
            ev.preventDefault();
            ev.stopPropagation();
            setTableOfContentsDrawerOpen(false);
            return;
        }

        // Don't hijack keystrokes the user is typing into a real input.
        if (isEditableTarget(ev.target)) return;

        var id = shortcutIdFromEvent(ev);
        if (!id) return;

        ev.preventDefault();
        ev.stopPropagation();
        postToHost({ type: "shortcut", id: id });
    }

    // ----- Bootstrap -----
    function onReady() {
        contentEl = document.getElementById("content");
        tocEl = document.getElementById("table-of-contents");
        tocOpenerEl = document.getElementById("table-of-contents-opener");
        tocTitleEl = document.getElementById("table-of-contents-title");
        tocEmptyEl = document.getElementById("table-of-contents-empty");
        tocListEl = document.getElementById("table-of-contents-list");
        applyStringsToTableOfContents();
        contentEl.addEventListener("click", handleClick, true);
        if (tocOpenerEl) {
            tocOpenerEl.addEventListener("click", function (ev) {
                ev.preventDefault();
                ev.stopPropagation();
                toggleTableOfContentsDrawer();
            });
        }
        document.addEventListener("click", function (ev) {
            if (!tocDrawerOpen) return;
            if (tocEl && tocEl.contains(ev.target)) return;
            if (tocOpenerEl && tocOpenerEl.contains(ev.target)) return;
            setTableOfContentsDrawerOpen(false);
        });

        // Capture-phase so we see the key before any child handler can
        // swallow it (e.g. KaTeX-rendered widgets).
        window.addEventListener("keydown", handleAcceleratorKey, true);

        // Ctrl+Wheel zoom + trackpad pinch (Chromium delivers precision-
        // touchpad pinches as ctrlKey wheel events). passive:false is
        // required for preventDefault(); capture:true ensures inner
        // scrollables don't eat the event first.
        window.addEventListener("wheel", handleWheelZoom, { passive: false, capture: true });
        window.addEventListener("scroll", scheduleActiveHeadingUpdate, { passive: true });
        window.addEventListener("resize", scheduleActiveHeadingUpdate);

        // Make sure a zoom change that's still in the debounce window when
        // the window/tab goes away gets persisted instead of being silently
        // dropped. Both events fire on WebView2 navigation/close paths.
        window.addEventListener("pagehide", flushZoomPost);
        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState === "hidden") flushZoomPost();
        });

        installPurifyHooks();
        initMermaid(currentThemeType);
        // Construct the Mermaid zoom modal eagerly so we can apply localized
        // strings as soon as the host posts them.
        ensureZoomModal();

        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener("message", function (ev) {
                var msg = ev.data;
                if (!msg || typeof msg !== "object") return;
                switch (msg.type) {
                    case "render":
                        render(
                            msg.markdown,
                            msg.sourcePath,
                            msg.contentBaseUri,
                            msg.theme,
                            msg.themeType,
                            msg.themeIsDark,
                            msg.themeVars);
                        break;
                    case "strings":
                        if (msg.strings && typeof msg.strings === "object") {
                            setStrings(msg.strings);
                        }
                        break;
                    case "theme":
                        setTheme(msg.theme, msg.themeType, msg.themeIsDark, msg.themeVars);
                        break;
                    case "zoom":
                        setZoomFromHost(msg.factor);
                        break;
                    case "contentMaxWidth":
                        // CSS の var(--skim-content-max) を body 上に inline で上書き。
                        // value は host 側で "760px" / "960px" / "1200px" / "none" のいずれかに正規化済み。
                        // 文字列以外が来た場合は防御で無視する。
                        if (typeof msg.value === "string" && msg.value.length > 0) {
                            try {
                                document.body.style.setProperty("--skim-content-max", msg.value);
                            } catch (e) { /* best-effort */ }
                        }
                        break;
                    case "tocVisible":
                        setTableOfContentsVisible(!!msg.visible);
                        break;
                    case "search":
                        applySearch(msg.query, !!msg.caseSensitive, true);
                        break;
                    case "search/next": searchNext(); break;
                    case "search/prev": searchPrev(); break;
                    case "search/clear": searchClear(); break;
                    case "scrollToAnchor":
                        scrollToAnchorByHash(msg.hash || "");
                        break;
                    case "selectAll":
                        try {
                            var range = document.createRange();
                            range.selectNodeContents(contentEl);
                            var sel = window.getSelection();
                            sel.removeAllRanges();
                            sel.addRange(range);
                        } catch (e) { /* best-effort */ }
                        break;
                    case "copySelection":
                        try {
                            var s = window.getSelection().toString();
                            if (s) postToHost({ type: "copy", text: s });
                        } catch (e) { /* best-effort */ }
                        break;
                    case "empty":
                        hasRenderedDocument = false;
                        currentTocItems = [];
                        activeHeadingID = "";
                        tocDrawerOpen = false;
                        if (tocListEl) tocListEl.textContent = "";
                        contentEl.innerHTML = '<div class="skim-empty">No Markdown selected.</div>';
                        updateTableOfContentsVisibility();
                        break;
                }
            });
        }

        postToHost({ type: "ready" });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", onReady);
    } else {
        onReady();
    }

    // Expose pure-DOM helpers for jsdom-based smoke tests. Production code
    // never reads this; it's only here so smoke-renderer.js can exercise
    // edge cases without booting real mermaid.
    if (typeof window !== "undefined") {
        window.__skimDownInternal = {
            normalizeMermaidSvgSizes: normalizeMermaidSvgSizes,
            setStrings: setStrings,
            t: t,
            getZoomModal: function () { return zoomModal; },
            openZoomModal: openZoomModal,
            closeZoomModal: closeZoomModal,
            isZoomModalOpen: isZoomModalOpen,
            getZoomState: function () { return { scale: zoomState.scale, tx: zoomState.tx, ty: zoomState.ty }; },
            bindZoomToMermaidWraps: bindZoomToMermaidWraps,
            simulateMermaidWrapClick: function (wrap, opts) {
                onMermaidWrapClick({
                    currentTarget: wrap,
                    target: (opts && opts.target) || wrap,
                });
            },
            handleWheelZoom: handleWheelZoom,
            applyZoomLocal: applyZoomLocal,
            initMermaid: initMermaid,
            headingElements: headingElements,
            tableOfContents: tableOfContents,
            renderTableOfContents: renderTableOfContents,
            setTableOfContentsVisible: setTableOfContentsVisible,
            setTableOfContentsDrawerOpen: setTableOfContentsDrawerOpen,
            isTableOfContentsDrawerOpen: function () { return tocDrawerOpen; },
            updateActiveHeading: updateActiveHeading,
        };
    }
})();
