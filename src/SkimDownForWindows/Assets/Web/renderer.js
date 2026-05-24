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
    var currentSourceDir = "";
    var currentContentBaseUri = "";
    var lastRenderedMarkdown = "";
    var lastRenderedHtml = "";
    var currentTheme = "light";       // "light" | "dark" | "custom"
    var currentThemeType = "light";   // "light" | "dark" — drives hljs + Mermaid choice
    var currentThemeIsDark = false;
    var appliedCustomVars = [];       // CSS variable names currently set on documentElement
    var mermaidReady = false;

    // ----- Search state -----
    var search = {
        query: "",
        caseSensitive: false,
        hits: [],          // Array<HTMLElement>
        current: -1,
    };

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
                return '<div class="skim-mermaid-wrap">' +
                       '<pre class="mermaid" data-source="' + escapeAttr(rawCode) + '">' +
                       escapeHtml(rawCode) +
                       '</pre></div>';
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
        var rootStyle = window.getComputedStyle(document.documentElement);
        function cssVar(name) {
            var v = rootStyle.getPropertyValue(name);
            return v ? v.trim() : "";
        }
        var bg = cssVar("--skim-bg");
        var fg = cssVar("--skim-fg");
        var soft = cssVar("--skim-soft-strong");
        var accent = cssVar("--skim-link");
        var border = cssVar("--skim-border");
        var themeVariables = { fontFamily: "inherit" };
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
                fontFamily: "inherit",
                themeVariables: themeVariables,
            });
            mermaidReady = true;
        } catch (e) {
            logToHost("mermaid.initialize failed: " + (e && e.message ? e.message : e));
        }
    }

    function renderMermaidBlocks() {
        if (!window.mermaid || !contentEl) return;
        var blocks = contentEl.querySelectorAll("pre.mermaid");
        if (blocks.length === 0) return;
        try {
            window.mermaid.run({
                querySelector: "#content pre.mermaid",
                suppressErrors: true,
            });
        } catch (e) {
            logToHost("mermaid.run failed: " + (e && e.message ? e.message : e));
        }
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
        var blocks = contentEl.querySelectorAll("pre.mermaid, .skim-mermaid-wrap > pre, .skim-mermaid-wrap svg");
        if (blocks.length === 0) return;
        // Reset each Mermaid block back to its source so .run() will re-render under the new theme.
        var wraps = contentEl.querySelectorAll(".skim-mermaid-wrap");
        wraps.forEach(function (wrap) {
            var src = wrap.getAttribute("data-source");
            if (src == null) {
                var existing = wrap.querySelector("pre.mermaid");
                src = existing ? (existing.getAttribute("data-source") || existing.textContent) : "";
                wrap.setAttribute("data-source", src);
            }
            wrap.innerHTML = '';
            var pre = document.createElement("pre");
            pre.className = "mermaid";
            pre.setAttribute("data-source", src);
            pre.textContent = src;
            wrap.appendChild(pre);
        });
        renderMermaidBlocks();
    }

    // Apply CSS variables for a custom theme via inline style on documentElement.
    // Only keys with the "--skim-" prefix are honored to prevent CSS injection
    // through arbitrary property names. Values are inserted as-is — the host
    // is responsible for validating them on the C# side.
    function applyThemeVars(themeVars) {
        // Strip any previously applied custom vars so stale values don't leak
        // into the next theme.
        var docEl = document.documentElement;
        for (var i = 0; i < appliedCustomVars.length; i++) {
            try { docEl.style.removeProperty(appliedCustomVars[i]); } catch (e) { /* best-effort */ }
        }
        appliedCustomVars = [];

        if (!themeVars || typeof themeVars !== "object") return;
        for (var name in themeVars) {
            if (!Object.prototype.hasOwnProperty.call(themeVars, name)) continue;
            if (typeof name !== "string" || name.indexOf("--skim-") !== 0) continue;
            var value = themeVars[name];
            if (typeof value !== "string" || value.length === 0) continue;
            try {
                docEl.style.setProperty(name, value);
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
        var f = parseFloat(factor);
        if (!isFinite(f) || f <= 0) return;
        document.body.style.zoom = String(f);
    }

    function rewriteRelativeUrls(html) {
        if (!currentContentBaseUri || !currentSourceDir) return html;
        // Use a DOM walker to rewrite src/href that look like relative paths.
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
        currentContentBaseUri = contentBaseUri || "";
        // sourceDir is the relative folder portion of sourcePath, forward-slash form.
        currentSourceDir = "";
        if (sourcePath && typeof sourcePath === "string") {
            var idx = sourcePath.lastIndexOf("/");
            if (idx >= 0) currentSourceDir = sourcePath.substring(0, idx);
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

        // DOMPurify strips `data-source` on <pre class="mermaid"> when the
        // value contains characters it flags as risky (e.g. raw `>`), so
        // re-attach it from the post-sanitize textContent. Mermaid replaces
        // textContent with SVG once it runs, so theme-refresh later needs
        // this attribute to recover the original source.
        if (contentEl) {
            contentEl.querySelectorAll(".skim-mermaid-wrap > pre.mermaid").forEach(function (pre) {
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
        normalizeTaskLists();
        applyGithubAlerts();
        convertBacktickMath();
        applyColorSwatches();

        // Mermaid renders AFTER sanitization (it injects SVG into the live DOM).
        // We pin securityLevel: "strict" and constrain inputs to limit the blast
        // radius even though the markdown is locally-opened content.
        renderMermaidBlocks();

        try { window.scrollTo(0, 0); } catch (e) { /* test envs may not implement scrollTo */ }

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
        var href = anchor.getAttribute("href");
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
        contentEl.addEventListener("click", handleClick, true);

        // Capture-phase so we see the key before any child handler can
        // swallow it (e.g. KaTeX-rendered widgets).
        window.addEventListener("keydown", handleAcceleratorKey, true);

        installPurifyHooks();
        initMermaid(currentThemeType);

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
                    case "theme":
                        setTheme(msg.theme, msg.themeType, msg.themeIsDark, msg.themeVars);
                        break;
                    case "zoom":
                        setZoom(msg.factor);
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
                        contentEl.innerHTML = '<div class="skim-empty">No Markdown selected.</div>';
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
})();
