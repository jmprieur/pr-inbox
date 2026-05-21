// last-visited.js
//
// Lightweight "where was I?" affordance. When the Review page opens, it
// stashes the PR URL in localStorage via mark(url). When the Inbox page
// renders, flash() pulls that URL back out, finds the matching row,
// scrolls it into view, and pulses it for ~2s so the eye lands exactly
// where it left off.
//
// Retries finding the row for up to ~3s because Blazor Server may render
// the page shell before the row data has streamed in.
(function () {
    'use strict';

    var STORAGE_KEY = 'prinbox.lastVisitedUrl';
    var PULSE_CLASS = 'row-just-returned';
    var PULSE_MS = 2200; // matches CSS animation duration
    var RETRY_MS = 150;
    var RETRY_BUDGET_MS = 3000;

    function mark(url) {
        if (!url) return;
        try { localStorage.setItem(STORAGE_KEY, url); } catch (_) { /* private mode */ }
    }

    function clear() {
        try { localStorage.removeItem(STORAGE_KEY); } catch (_) { /* ignore */ }
    }

    function findRow(url) {
        // Escape the URL for the attribute selector (it contains `:` `/` `?` etc.).
        if (window.CSS && CSS.escape) {
            return document.querySelector('tr[data-pr-url="' + CSS.escape(url) + '"]');
        }
        // Fallback: linear scan.
        var rows = document.querySelectorAll('tr[data-pr-url]');
        for (var i = 0; i < rows.length; i++) {
            if (rows[i].getAttribute('data-pr-url') === url) return rows[i];
        }
        return null;
    }

    function pulse(row) {
        row.classList.remove(PULSE_CLASS);
        // Force reflow so the animation restarts even if the class is re-applied.
        void row.offsetWidth;
        row.classList.add(PULSE_CLASS);
        setTimeout(function () { row.classList.remove(PULSE_CLASS); }, PULSE_MS);
    }

    function flash() {
        var url;
        try { url = localStorage.getItem(STORAGE_KEY); } catch (_) { return; }
        if (!url) return;

        var deadline = Date.now() + RETRY_BUDGET_MS;
        function attempt() {
            var row = findRow(url);
            if (row) {
                clear();
                try { row.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
                catch (_) { row.scrollIntoView(); }
                pulse(row);
                return;
            }
            if (Date.now() < deadline) {
                setTimeout(attempt, RETRY_MS);
            } else {
                // Row never showed up (filter changed, PR closed, etc.) -- give up
                // silently and clear so we don't keep retrying on subsequent loads.
                clear();
            }
        }
        attempt();
    }

    window.prInboxLastVisited = { mark: mark, flash: flash, clear: clear };
})();
