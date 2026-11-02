// =============================================================================
// Sign-up page (Views/Account/Register.cshtml).
// Sends the form as JSON to POST /api/v1/auth/register. On 201 Created the browser goes to the
// sign-in page; otherwise the API's problem details are shown next to the right field.
// The server logs every outcome (UserRegistrationService, AuthController, ApiExceptionHandler);
// this script only writes to the browser console for failures the user cannot fix, together
// with the correlation id that finds the matching server log entry. The password is never logged.
// =============================================================================
(function () {
    'use strict';

    var form = document.querySelector('form[data-register-api]');
    if (!form) {
        return;
    }

    var submitButton = form.querySelector('[data-register-submit]');
    var spinner = form.querySelector('[data-register-spinner]');
    var formError = form.querySelector('[data-register-error]');

    // API error codes that belong to one field (docs/API.md).
    var fieldForErrorCode = {
        'Account.DuplicateEmail': 'Email',
        'Account.InvalidEmail': 'Email',
        'Account.WeakPassword': 'Password'
    };

    var genericFailure = 'Something went wrong while creating your account. Please try again.';

    // The button is disabled in the HTML so the form cannot be submitted before this script runs.
    submitButton.disabled = false;

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        clearErrors();

        // Client-side validation (jquery-validation-unobtrusive) is a convenience; the API validates again.
        if (window.jQuery && window.jQuery.fn.valid && !window.jQuery(form).valid()) {
            return;
        }

        setBusy(true);
        submitRegistration()
            .then(function (succeeded) {
                if (!succeeded) {
                    setBusy(false);
                }
            })
            .catch(function (error) {
                // A bug in this script, not a server response.
                console.error('Sign-up failed unexpectedly in the browser.', error);
                showFormError(genericFailure);
                setBusy(false);
            });
    });

    /** Calls the API. Resolves true when the account was created (and navigation started). */
    async function submitRegistration() {
        var response;
        try {
            response = await fetch(form.dataset.registerApi, {
                method: 'POST',
                credentials: 'omit', // the API never uses the sign-in cookie
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json, application/problem+json'
                },
                body: JSON.stringify({
                    email: form.elements.Email.value.trim(),
                    password: form.elements.Password.value
                })
            });
        } catch (networkError) {
            console.error('Sign-up request did not reach the server.', networkError);
            showFormError('The server could not be reached. Check your connection and try again.');
            return false;
        }

        if (response.status === 201) {
            window.location.assign(form.dataset.loginUrl);
            return true;
        }

        var problem = await readProblem(response);
        showFailure(response, problem);
        return false;
    }

    /** The problem details body, or null if the response is not JSON (e.g. a proxy error page). */
    async function readProblem(response) {
        var contentType = response.headers.get('Content-Type') || '';
        if (contentType.indexOf('json') === -1) {
            return null;
        }

        try {
            return await response.json();
        } catch (parseError) {
            console.warn('Sign-up response was not valid JSON.', parseError);
            return null;
        }
    }

    function showFailure(response, problem) {
        var code = problem && problem.code;

        // 400 from model validation: messages per field, e.g. { errors: { Email: ["..."] } }.
        if (problem && problem.errors) {
            Object.keys(problem.errors).forEach(function (field) {
                var message = problem.errors[field][0];
                if (!showFieldError(field, message)) {
                    showFormError(message);
                }
            });
            return;
        }

        // Duplicate e-mail, invalid e-mail, weak password: show next to the field.
        if (code && fieldForErrorCode[code] && problem.detail) {
            showFieldError(fieldForErrorCode[code], problem.detail);
            return;
        }

        if (response.status === 429) {
            showFormError('Too many sign-up attempts from your network. ' + retryAfterText(response));
            return;
        }

        if (response.status < 500 && problem && problem.detail) {
            showFormError(problem.detail);
            return;
        }

        // Server error or unexpected response: give the user the reference the logs are indexed by.
        var correlationId = (problem && problem.correlationId) || response.headers.get('X-Correlation-ID');
        console.error('Sign-up failed.', { status: response.status, code: code, correlationId: correlationId });
        showFormError(genericFailure + (correlationId ? ' Reference: ' + correlationId : ''));
    }

    function retryAfterText(response) {
        var seconds = parseInt(response.headers.get('Retry-After'), 10);
        if (isNaN(seconds)) {
            return 'Please try again later.';
        }

        var minutes = Math.ceil(seconds / 60);
        return 'Please try again in ' + minutes + (minutes === 1 ? ' minute.' : ' minutes.');
    }

    /** Shows the message under the field; returns false if the form has no such field. */
    function showFieldError(fieldName, message) {
        var input = form.elements[fieldName];
        var messageElement = form.querySelector('[data-valmsg-for="' + fieldName + '"]');
        if (!input || !messageElement) {
            return false;
        }

        input.classList.add('is-invalid');
        input.setAttribute('aria-invalid', 'true');
        messageElement.textContent = message;
        messageElement.classList.remove('field-validation-valid');
        messageElement.classList.add('field-validation-error');
        input.focus();
        return true;
    }

    function showFormError(message) {
        formError.textContent = message;
        formError.classList.remove('d-none');
        formError.focus();
    }

    function clearErrors() {
        formError.textContent = '';
        formError.classList.add('d-none');

        form.querySelectorAll('.is-invalid').forEach(function (input) {
            input.classList.remove('is-invalid');
            input.removeAttribute('aria-invalid');
        });

        // Messages from the previous server response; client-side validation re-adds its own.
        form.querySelectorAll('[data-valmsg-for]').forEach(function (messageElement) {
            messageElement.textContent = '';
        });
    }

    function setBusy(busy) {
        submitButton.disabled = busy;
        spinner.classList.toggle('d-none', !busy);
        form.setAttribute('aria-busy', busy ? 'true' : 'false');
    }
})();
