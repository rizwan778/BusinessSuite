// =============================================================================
// BusinessSuite site-wide JavaScript.
// Rules (enforced by the Content-Security-Policy):
//   - all scripts live in files under wwwroot/js; no inline <script> blocks or onclick="..."
//   - JavaScript improves the experience only; every rule and calculation is enforced
//     on the server (prices, totals, permissions are never trusted from the browser)
// =============================================================================
(function () {
    'use strict';

    // Bootstrap tooltips for any element with data-bs-toggle="tooltip".
    function initTooltips() {
        if (!window.bootstrap || !window.bootstrap.Tooltip) {
            return;
        }

        document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function (element) {
            window.bootstrap.Tooltip.getOrCreateInstance(element);
        });
    }

    // Prevent double submission (e.g. confirming an invoice twice): forms marked with
    // data-disable-on-submit disable their submit buttons once the browser really submits.
    // The check is deferred so client-side validation can cancel the submit first.
    function initSingleSubmitForms() {
        document.querySelectorAll('form[data-disable-on-submit]').forEach(function (form) {
            form.addEventListener('submit', function (event) {
                window.setTimeout(function () {
                    if (event.defaultPrevented) {
                        return;
                    }

                    form.querySelectorAll('button[type="submit"], input[type="submit"]').forEach(function (button) {
                        button.disabled = true;
                    });
                }, 0);
            });
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        initTooltips();
        initSingleSubmitForms();
    });
})();
