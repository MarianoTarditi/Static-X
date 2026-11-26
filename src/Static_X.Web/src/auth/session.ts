import { QueryClient } from '@tanstack/react-query';
import { ApiError, request, safeError } from '@/lib/api';

export interface Account { id: string; gymId: number | null; email: string; role: 1 | 2 | 3 | 4 }
export interface LoginInput { gymId: number | null; email: string; password: string }
export type SessionState =
  | { status: 'pending' }
  | { status: 'error'; message: string }
  | { status: 'anonymous'; busy: boolean; message?: string }
  | { status: 'authenticated'; account: Account; busy: boolean; message?: string };

export const isStaff = (account: Account) => account.role === 1 || account.role === 2;
export const roleLabel = (role: Account['role']) => ({ 1: 'Gym administrator', 2: 'Trainer', 3: 'Student', 4: 'Super administrator' })[role];

export function parseAccount(value: unknown): Account {
  if (!value || typeof value !== 'object') throw new Error('Invalid identity');
  const a = value as Record<string, unknown>;
  if (typeof a.id !== 'string' || !a.id || typeof a.email !== 'string' || !a.email ||
    ![1, 2, 3, 4].includes(a.role as number) ||
    (a.role === 4 ? a.gymId !== null : !Number.isSafeInteger(a.gymId) || (a.gymId as number) <= 0)) {
    throw new Error('Invalid identity');
  }
  return { id: a.id, email: a.email, role: a.role as Account['role'], gymId: a.gymId as number | null };
}

export function createSession() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 30_000, refetchOnWindowFocus: true }, mutations: { retry: false } },
  });
  let state: SessionState = { status: 'pending' };
  let started = false;
  let locked = false;
  let epoch = 0;
  let identityAbort = new AbortController();
  let csrf: string | null = null;
  const listeners = new Set<() => void>();
  const publish = (next: SessionState) => { state = next; listeners.forEach((notify) => notify()); };

  async function boundary() {
    epoch += 1;
    identityAbort.abort();
    identityAbort = new AbortController();
    csrf = null;
    await queryClient.cancelQueries();
    queryClient.clear();
  }

  async function freshCsrf() {
    csrf = null;
    const response = await request('/account/csrf', { signal: identityAbort.signal });
    const value: unknown = await response.json();
    if (!value || typeof value !== 'object' || !('token' in value) || typeof value.token !== 'string' || !value.token) {
      throw new Error('Missing request token');
    }
    csrf = value.token;
    return csrf;
  }

  async function restore() {
    if (locked) return;
    locked = true;
    publish({ status: 'pending' });
    await boundary();
    let identityConfirmed = false;
    try {
      const response = await request('/account/me', { signal: identityAbort.signal });
      const account = parseAccount(await response.json());
      identityConfirmed = true;
      await freshCsrf();
      publish({ status: 'authenticated', account, busy: false });
    } catch (error) {
      if (!identityConfirmed && error instanceof ApiError && error.status === 401) publish({ status: 'anonymous', busy: false });
      else publish({ status: 'error', message: safeError(error, 'Session could not be confirmed. Check your connection and try again.') });
    } finally { locked = false; }
  }

  async function login(input: LoginInput) {
    if (locked || state.status !== 'anonymous') return;
    locked = true;
    publish({ status: 'anonymous', busy: true });
    await boundary();
    let sent = false;
    let identityChanged = false;
    try {
      const token = await freshCsrf();
      sent = true;
      const response = await request('/account/login', {
        method: 'POST', signal: identityAbort.signal,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
        body: JSON.stringify(input),
      });
      identityChanged = true;
      const account = parseAccount(await response.json());
      // An anonymous request token cannot be reused with the new identity.
      await boundary();
      await freshCsrf();
      publish({ status: 'authenticated', account, busy: false });
    } catch (error) {
      csrf = null;
      if (!identityChanged && error instanceof ApiError && [400, 401, 403, 429].includes(error.status)) {
        publish({ status: 'anonymous', busy: false, message: error.status === 401
          ? 'Sign-in failed. Check your account type, gym ID, email and password.'
          : safeError(error, 'Unable to sign in. Please try again.') });
      } else if (sent) {
        // A lost response may still have set the cookie. Never claim anonymous here.
        publish({ status: 'error', message: 'Session could not be confirmed. Check your connection and try again.' });
      } else publish({ status: 'anonymous', busy: false, message: 'Unable to prepare secure sign-in. Check your connection and try again.' });
    } finally { locked = false; }
  }

  async function logout() {
    if (locked || state.status !== 'authenticated') return;
    const account = state.account;
    locked = true;
    publish({ status: 'authenticated', account, busy: true });
    await boundary();
    try {
      const token = await freshCsrf();
      const response = await request('/account/logout', { method: 'POST', headers: { 'X-CSRF-TOKEN': token }, signal: identityAbort.signal });
      if (response.status !== 204) throw new Error('Unexpected sign-out response');
      await boundary();
      // Sign-out is confirmed even if fetching the next anonymous token fails.
      try { await freshCsrf(); } catch { csrf = null; }
      publish({ status: 'anonymous', busy: false });
    } catch {
      csrf = null;
      publish({ status: 'authenticated', account, busy: false,
        message: 'Sign-out was not confirmed. Your session may still be active. Please try again.' });
    } finally { locked = false; }
  }

  async function privateGet(path: string, signal: AbortSignal) {
    if (state.status !== 'authenticated' || state.busy) throw new Error('Session unavailable');
    const current = epoch;
    const combined = AbortSignal.any([signal, identityAbort.signal]);
    try {
      const response = await request(path, { signal: combined });
      const data: unknown = await response.json();
      if (current !== epoch || combined.aborted) throw new DOMException('Cancelled', 'AbortError');
      return data;
    } catch (error) {
      if (current === epoch && error instanceof ApiError && error.status === 401) {
        // Hide private UI immediately, then cancel and remove all identity-bound data.
        publish({ status: 'anonymous', busy: false, message: 'Your session has expired. Please sign in again.' });
        await boundary();
      }
      throw error;
    }
  }

  return {
    queryClient, restore, login, logout, privateGet,
    start: () => { if (!started) { started = true; void restore(); } },
    getSnapshot: () => state,
    subscribe: (notify: () => void) => { listeners.add(notify); return () => { listeners.delete(notify); }; },
  };
}

export type Session = ReturnType<typeof createSession>;
