// Product form: changing the category re-renders the form with that category's attributes.
// The server keeps the values already typed (matched by attribute code); nothing is saved.
(function () {
    'use strict';

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('select[data-reload-schema]').forEach(function (select) {
            select.addEventListener('change', function () {
                var form = select.closest('form');
                var flag = form && form.querySelector('input[name="ReloadSchema"][type="hidden"]');
                if (flag) {
                    flag.value = 'true';
                    form.submit();
                }
            });
        });
    });
})();
