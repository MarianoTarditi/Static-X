(() => {
    'use strict';

    let token = readInvitation();
    document.addEventListener('DOMContentLoaded', () => {
        const form = document.getElementById('activation-form');
        const fields = document.getElementById('activation-fields');
        const password = document.getElementById('new-password');
        const status = document.getElementById('activation-status');
        const reopen = 'Open the activation link from your invitation email.';

        form.addEventListener('submit', async (event) => {
            event.preventDefault();
            if (!token || fields.disabled || !password.value) return;

            fields.disabled = true;
            status.textContent = 'Activating your account…';
            const body = JSON.stringify({ token, password: password.value });
            password.value = '';
            const options = { mode: 'same-origin', credentials: 'same-origin', cache: 'no-store',
                redirect: 'error', referrerPolicy: 'no-referrer' };

            try {
                const csrfResponse = await fetch('/api/account/csrf', options);
                if (!csrfResponse.ok || !token) throw new Error();
                const csrf = (await csrfResponse.json()).token;
                if (typeof csrf !== 'string' || !csrf || !token) throw new Error();

                const response = await fetch('/api/account/activation', {
                    ...options,
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
                    body
                });
                if (response.status === 200) {
                    token = '';
                    form.hidden = true;
                    status.textContent = 'Account activated. You can now sign in separately.';
                } else if (token) {
                    status.textContent = response.status === 400
                        ? 'Activation was not accepted. Try a different password, or request a new invitation if this link has expired or was already used.'
                        : 'Could not complete activation. Please try again.';
                }
            } catch {
                if (token) status.textContent = 'Could not complete activation. Please try again.';
            } finally {
                if (token) {
                    fields.disabled = false;
                    password.focus();
                }
            }
        });

        window.addEventListener('pagehide', () => {
            if (token) status.textContent = reopen;
            token = '';
            password.value = '';
            fields.disabled = true;
        });

        fields.disabled = !token;
        status.textContent = token ? 'Enter your new password to activate your account.' : reopen;
    }, { once: true });

    function readInvitation() {
        const fragment = window.location.hash;
        const prefix = '#activation=';
        try {
            if (fragment || window.location.search) window.history.replaceState(null, '', '/activate');
            if (!fragment.startsWith(prefix)) return '';
            const value = decodeURIComponent(fragment.slice(prefix.length));
            return value.length === 64 && !/[^a-f\d]/i.test(value) ? value : '';
        } catch {
            // Do not enable submission if the URL cannot be scrubbed or decoded.
            return '';
        }
    }
})();
