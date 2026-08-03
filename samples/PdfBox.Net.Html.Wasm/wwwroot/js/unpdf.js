const previewSessions = new Map();
let blobSupportOverride;

function requirePreviewSession(sessionId) {
    const session = previewSessions.get(sessionId);
    if (!session) {
        throw new Error(`Unknown preview session '${sessionId}'.`);
    }

    return session;
}

function replaceAllLiteral(value, search, replacement) {
    return search ? value.split(search).join(replacement) : value;
}

function isAppleMobileWebKit() {
    const navigator = globalThis.navigator;
    if (!navigator) {
        return false;
    }

    const userAgent = navigator.userAgent ?? "";
    const isAppleWebKit = /AppleWebKit\//.test(userAgent);
    const isMobileAppleDevice = /\b(iPhone|iPad|iPod)\b/.test(userAgent)
        || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
    return isAppleWebKit && isMobileAppleDevice;
}

window.unpdf = {
    normalizeCompatibility(value, form) {
        return value.normalize(form);
    },
    memory: {
        snapshot() {
            const runtime = globalThis.getDotnetRuntime?.(0);
            return {
                javaScriptHeapBytes: Number.isFinite(performance.memory?.usedJSHeapSize)
                    ? performance.memory.usedJSHeapSize
                    : null,
                wasmMemoryBytes: runtime?.Module?.HEAP8?.buffer?.byteLength ?? null
            };
        }
    },
    preview: {
        supportsBlobUrls() {
            if (blobSupportOverride !== undefined) {
                return blobSupportOverride;
            }

            return typeof Blob === "function"
                && typeof URL !== "undefined"
                && typeof URL.createObjectURL === "function"
                && typeof URL.revokeObjectURL === "function"
                // iPhone and iPad WebKit expose the Blob URL APIs but can render
                // a Blob-backed HTML iframe as an empty document. Use the existing
                // srcdoc path there instead of treating API presence as sufficient.
                && !isAppleMobileWebKit();
        },
        setBlobSupportOverride(value) {
            blobSupportOverride = value === null ? undefined : Boolean(value);
        },
        begin() {
            const sessionId = globalThis.crypto?.randomUUID?.()
                ?? `preview-${Date.now()}-${Math.random().toString(16).slice(2)}`;
            previewSessions.set(sessionId, {
                replacements: new Map(),
                urls: []
            });
            return sessionId;
        },
        addAsset(sessionId, relativePath, fileName, contentType, data) {
            const session = requirePreviewSession(sessionId);
            const url = URL.createObjectURL(new Blob([data], { type: contentType }));
            session.urls.push(url);
            session.replacements.set(relativePath, url);
            session.replacements.set(fileName, url);
        },
        complete(sessionId, html, cssPath, css) {
            const session = requirePreviewSession(sessionId);
            const replacements = [...session.replacements.entries()]
                .sort((left, right) => right[0].length - left[0].length);
            for (const [path, url] of replacements) {
                html = replaceAllLiteral(html, path, url);
                css = replaceAllLiteral(css, path, url);
            }

            const cssUrl = URL.createObjectURL(new Blob([css], { type: "text/css" }));
            session.urls.push(cssUrl);
            html = replaceAllLiteral(html, cssPath, cssUrl);

            const documentUrl = URL.createObjectURL(new Blob([html], { type: "text/html" }));
            session.urls.push(documentUrl);
            session.documentUrl = documentUrl;
            return documentUrl;
        },
        release(sessionId) {
            const session = previewSessions.get(sessionId);
            if (!session) {
                return;
            }

            for (const url of session.urls) {
                URL.revokeObjectURL(url);
            }
            previewSessions.delete(sessionId);
        },
        activeSessionCount() {
            return previewSessions.size;
        }
    }
};
