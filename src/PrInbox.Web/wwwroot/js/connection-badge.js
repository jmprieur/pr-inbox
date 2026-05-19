// connection-badge.js
//
// Diagnostic overlay that surfaces whether the Blazor Server circuit
// (SignalR) actually established. Without this, a failed circuit looks
// identical to a working page -- buttons just silently do nothing.
//
// Wiring: ConnectionBadge.razor renders inside an InteractiveServer
// boundary. When the circuit attaches, its OnAfterRenderAsync calls
// prInboxConnectionBadge.markAlive() via JS interop. If markAlive
// never fires within 10s, the badge flips to "disconnected" and tells
// the user what's likely wrong (corporate firewall / proxy on Edge,
// stale page, etc.).
(function () {
    'use strict';

    var badge;
    var alive = false;
    var startedAt = Date.now();

    function ensureBadge() {
        if (badge) return badge;
        badge = document.getElementById('pr-inbox-conn-badge');
        if (!badge) {
            badge = document.createElement('div');
            badge.id = 'pr-inbox-conn-badge';
            badge.setAttribute('role', 'status');
            badge.style.cssText =
                'position:fixed;bottom:0.5rem;right:0.5rem;' +
                'padding:0.3rem 0.6rem;border-radius:3px;' +
                'font:0.78rem system-ui,-apple-system,Segoe UI,sans-serif;' +
                'z-index:9999;transition:opacity .4s;' +
                'box-shadow:0 1px 4px rgba(0,0,0,0.4);' +
                'cursor:default;user-select:none;max-width:22rem';
            document.body.appendChild(badge);
        }
        return badge;
    }

    function setState(state) {
        var b = ensureBadge();
        b.dataset.state = state;
        b.style.opacity = '1';
        if (state === 'connecting') {
            b.textContent = '● connecting…';
            b.style.background = '#3a2a14';
            b.style.color = '#fc6';
            b.style.border = '1px solid #5a4020';
            b.title = 'Waiting for Blazor circuit (SignalR) to connect. ' +
                'If this stays here >10s, buttons will not respond.';
        } else if (state === 'connected') {
            b.textContent = '● connected';
            b.style.background = '#1a3a1a';
            b.style.color = '#7c7';
            b.style.border = '1px solid #2f5a2f';
            b.title = 'Blazor circuit (SignalR) is active. Buttons respond.';
            // Fade so it's not visually noisy once we know it works.
            setTimeout(function () { if (b.dataset.state === 'connected') b.style.opacity = '0.35'; }, 3000);
        } else if (state === 'disconnected') {
            b.textContent = '● disconnected — clicks will not respond';
            b.style.background = '#3a1414';
            b.style.color = '#f88';
            b.style.border = '1px solid #6a2222';
            b.title = 'Blazor circuit failed to connect within 10s. ' +
                'Common causes: corporate firewall or proxy blocking WebSockets to localhost, ' +
                'browser extension interfering with blazor.web.js, or a stale page ' +
                '(try Ctrl+F5 to hard-reload). On Microsoft Edge in a managed/Intune environment, ' +
                'WebSocket upgrades to http://localhost are sometimes blocked by the device policy.';
        }
    }

    // Start in "connecting" immediately so the badge is visible.
    setState('connecting');

    // If nothing happens for 10s, declare disconnected so the user knows
    // why their clicks are not working.
    setTimeout(function () {
        if (!alive) setState('disconnected');
    }, 10000);

    window.prInboxConnectionBadge = {
        markAlive: function () {
            alive = true;
            setState('connected');
        },
        // Manual escape hatch for diagnostics from the console:
        // window.prInboxConnectionBadge.show() / .hide()
        show: function () { if (badge) badge.style.display = 'block'; },
        hide: function () { if (badge) badge.style.display = 'none'; },
        msSinceStart: function () { return Date.now() - startedAt; }
    };
})();
