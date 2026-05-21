/* SkimDown WebView2 renderer.
 *
 * Bridge: receives JSON messages from the WinUI host via
 * `chrome.webview.postMessage`. Sends responses (link clicks, search counts)
 * back the same way.
 *
 * Message in:
 *   { type: "render", markdown, sourcePath, contentBaseUri, theme }
 *   { type: "theme",  theme }            // "light" | "dark"
 *   { type: "zoom",   factor }           // 0.5..3.0
 *   { type: "search", query, caseSensitive }
 *   { type: "search/next" } / { type: "search/prev" } / { type: "search/clear" }
 *
 * Message out:
 *   { type: "link",   href, kind }
 *   { type: "search/result", total, current }
 *   { type: "ready" }
 */

(function () {
    "use strict";

    var md = null;
    var contentEl = null;
    var currentSourceDir = "";
    var currentContentBaseUri = "";
    var lastRenderedMarkdown = "";
    var lastRenderedHtml = "";

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

    function ensureMarkdown() {
        if (md) return md;
        md = window.markdownit({
            html: true,
            linkify: true,
            breaks: false,
            typographer: true,
            highlight: function (code, lang) {
                try {
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
        return md;
    }

    function setTheme(theme) {
        var t = (theme || "light").toLowerCase();
        if (t !== "dark") t = "light";
        document.body.dataset.theme = t;
        var lightLink = document.getElementById("hljs-light");
        var darkLink  = document.getElementById("hljs-dark");
        if (lightLink && darkLink) {
            lightLink.disabled = (t === "dark");
            darkLink.disabled  = (t !== "dark");
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

    function render(markdown, sourcePath, contentBaseUri, theme) {
        if (typeof markdown !== "string") markdown = "";
        currentContentBaseUri = contentBaseUri || "";
        // sourceDir is the relative folder portion of sourcePath, forward-slash form.
        currentSourceDir = "";
        if (sourcePath && typeof sourcePath === "string") {
            var idx = sourcePath.lastIndexOf("/");
            if (idx >= 0) currentSourceDir = sourcePath.substring(0, idx);
        }
        if (theme) setTheme(theme);

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
                  USE_PROFILES: { html: true },
                  ADD_ATTR: ["target", "rel", "id"],
                  FORBID_TAGS: ["style", "script", "iframe", "object", "embed", "form"],
                  FORBID_ATTR: ["onerror", "onload", "onclick"],
              })
            : raw;

        lastRenderedMarkdown = markdown;
        lastRenderedHtml = clean;
        contentEl.innerHTML = clean;
        window.scrollTo(0, 0);

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

    function handleClick(ev) {
        var anchor = ev.target.closest && ev.target.closest("a");
        if (!anchor) return;
        var href = anchor.getAttribute("href");
        if (!href) return;

        if (href.charAt(0) === "#") {
            // In-document anchor: let the browser scroll, but also notify host so it
            // can persist scroll state later if desired.
            postToHost({ type: "link", href: href, kind: "anchor" });
            return; // do not preventDefault
        }

        ev.preventDefault();
        ev.stopPropagation();
        postToHost({
            type: "link",
            href: href,
            kind: isExternalUrl(href) ? "external" : "relative",
        });
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
                // Skip inside <pre> child? No — code text is also search-relevant.
                if (node.parentNode && node.parentNode.closest("mark.skim-search-hit")) {
                    return NodeFilter.FILTER_REJECT;
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
        el.scrollIntoView({ block: "center", behavior: "auto" });
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

    // ----- Bootstrap -----
    function onReady() {
        contentEl = document.getElementById("content");
        contentEl.addEventListener("click", handleClick, true);

        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener("message", function (ev) {
                var msg = ev.data;
                if (!msg || typeof msg !== "object") return;
                switch (msg.type) {
                    case "render":
                        render(msg.markdown, msg.sourcePath, msg.contentBaseUri, msg.theme);
                        break;
                    case "theme":
                        setTheme(msg.theme);
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
