import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { App } from '@/app';
import { createSession } from '@/auth/session';

const staff = { id: 'staff-1', gymId: 7, email: 'trainer@example.test', role: 2 };
const page = { items: [{ id: 12, nombre: 'Alex', apellido: 'River', email: 'alex@example.test', activo: true }], page: 1, pageSize: 20, totalCount: 21 };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;

function setup(handler: Handler, route = '/') {
  const fetcher = vi.fn((url: RequestInfo | URL, init?: RequestInit) => {
    const path = String(url);
    if (path === '/api/account/csrf') return Promise.resolve(json({ token: 'session-token' }));
    return Promise.resolve(handler(path, init ?? {}));
  });
  vi.stubGlobal('fetch', fetcher);
  const session = createSession();
  render(<MemoryRouter initialEntries={[route]}><App session={session} /></MemoryRouter>);
  return { fetcher, session };
}

afterEach(() => vi.unstubAllGlobals());

describe('session boundaries', () => {
  it('restores staff identity before requesting the private student list', async () => {
    let resolve!: (response: Response) => void;
    const me = new Promise<Response>((done) => { resolve = done; });
    const { fetcher } = setup((url) => url.endsWith('/me') ? me : json(page), '/students');
    expect(screen.getByRole('status')).toHaveTextContent('Checking your session');
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1));
    expect(fetcher.mock.calls[0][0]).toBe('/api/account/me');
    await act(async () => { resolve(json(staff)); });
    expect(await screen.findByText('Alex River')).toBeVisible();
    expect(screen.getByRole('link', { name: /Students/ })).toHaveAttribute('href', '/students');
  });

  it('uses an anonymous CSRF token for login and refreshes it before staff requests', async () => {
    const sequence: string[] = [];
    let signedIn = false;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      sequence.push(url);
      if (url.endsWith('/me')) return json({}, 401);
      if (url.endsWith('/csrf')) return json({ token: signedIn ? 'authenticated-token' : 'anonymous-token' });
      if (url.endsWith('/login')) {
        expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('anonymous-token');
        expect(JSON.parse(String(init?.body))).toEqual({ gymId: 7, email: 'trainer@example.test', password: 'secret-value' });
        signedIn = true;
        return json(staff);
      }
      return json(page);
    }));
    const session = createSession();
    render(<MemoryRouter><App session={session} /></MemoryRouter>);
    fireEvent.change(await screen.findByLabelText('Email'), { target: { value: staff.email } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret-value' } });
    fireEvent.change(screen.getByLabelText('Gym ID'), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Alex River')).toBeVisible();
    const loginIndex = sequence.indexOf('/api/account/login');
    expect(sequence.slice(loginIndex, loginIndex + 2)).toEqual(['/api/account/login', '/api/account/csrf']);
    expect(sequence.findIndex((url) => url.startsWith('/api/students'))).toBeGreaterThan(loginIndex + 1);
  });

  it('treats restore 401 as anonymous but keeps restore 403 as a recoverable session error', async () => {
    const { fetcher, session } = setup(() => json({}, 403));
    expect(await screen.findByRole('alert')).toHaveTextContent('Access is not available');
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    fetcher.mockImplementation(async () => json({}, 401));
    await act(async () => { await session.restore(); });
    expect(await screen.findByLabelText('Password')).toBeVisible();
  });

  it.each([3, 4])('does not request students for nonstaff role %s even at the staff URL', async (role) => {
    const { fetcher } = setup(() => json({ ...staff, role, gymId: role === 4 ? null : 7 }), '/students');
    expect(await screen.findByRole('heading', { name: 'Your account' })).toBeVisible();
    expect(screen.queryByRole('link', { name: /Students/ })).not.toBeInTheDocument();
    expect(fetcher.mock.calls.some(([url]) => String(url).startsWith('/api/students'))).toBe(false);
  });

  it('retains the session on failed logout and clears private cache on confirmed 204', async () => {
    let fail = true;
    const { session } = setup((url) => {
      if (url.endsWith('/me')) return json(staff);
      if (url.endsWith('/logout')) return fail ? json({ detail: 'SQL secret' }, 503) : new Response(null, { status: 204 });
      return json(page);
    });
    await screen.findByText('Alex River');
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-out was not confirmed');
    expect(screen.getByText(staff.email)).toBeVisible();
    expect(screen.queryByText('SQL secret')).not.toBeInTheDocument();
    fail = false;
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByLabelText('Password')).toBeVisible();
    expect(session.queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(screen.queryByText('Alex River')).not.toBeInTheDocument();
  });

  it('expires a staff session on list 401 without rendering a stale private result', async () => {
    const { session } = setup((url) => url.endsWith('/me') ? json(staff) : json({}, 401));
    expect(await screen.findByLabelText('Password')).toBeVisible();
    expect(session.queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(screen.queryByText('Alex River')).not.toBeInTheDocument();
  });

  it('rejects unsupported numeric roles instead of granting a staff route', async () => {
    const { fetcher } = setup(() => json({ ...staff, role: 99 }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Session could not be confirmed');
    expect(fetcher.mock.calls.some(([url]) => String(url).startsWith('/api/students'))).toBe(false);
  });

  it('keeps a failed identity-bound token refresh behind the session gate after successful login', async () => {
    let signedIn = false;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/me')) return json({}, 401);
      if (url.endsWith('/csrf')) return signedIn ? json({}, 403) : json({ token: 'anonymous-token' });
      if (url.endsWith('/login')) { signedIn = true; return json(staff); }
      throw new Error('Unexpected private request');
    }));
    render(<MemoryRouter><App session={createSession()} /></MemoryRouter>);
    fireEvent.change(await screen.findByLabelText('Email'), { target: { value: staff.email } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.change(screen.getByLabelText('Gym ID'), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Session could not be confirmed');
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    expect(screen.queryByText('Alex River')).not.toBeInTheDocument();
  });

  it('submits explicit global login without a gym and renders only the account landing', async () => {
    let submitted: unknown;
    setup((url, init) => {
      if (url.endsWith('/me')) return json({}, 401);
      if (url.endsWith('/login')) {
        submitted = JSON.parse(String(init.body));
        return json({ ...staff, role: 4, gymId: null });
      }
      throw new Error('Unexpected private request');
    });
    fireEvent.change(await screen.findByLabelText('Account type'), { target: { value: 'global' } });
    expect(screen.queryByLabelText('Gym ID')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: staff.email } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('heading', { name: 'Your account' })).toBeVisible();
    expect(submitted).toEqual({ gymId: null, email: staff.email, password: 'secret' });
  });

  it('rejects credentials without leaking server details or retrying the unsafe request', async () => {
    let loginCalls = 0;
    setup((url) => {
      if (url.endsWith('/me')) return json({}, 401);
      loginCalls += 1;
      return json({ detail: 'private credential details' }, 401);
    });
    fireEvent.change(await screen.findByLabelText('Email'), { target: { value: staff.email } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong' } });
    fireEvent.change(screen.getByLabelText('Gym ID'), { target: { value: '7' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in failed');
    expect(screen.getByLabelText('Password')).toHaveValue('');
    expect(screen.queryByText('private credential details')).not.toBeInTheDocument();
    expect(loginCalls).toBe(1);
  });

  it('recovers a network restore failure only after the user retries', async () => {
    let fail = true;
    setup((url) => {
      if (fail) throw new TypeError('private network details');
      return url.endsWith('/me') ? json(staff) : json(page);
    });
    expect(await screen.findByRole('alert')).toHaveTextContent('Session could not be confirmed');
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    fail = false;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Alex River')).toBeVisible();
  });

  it('ignores a late private response after logout even when fetch does not honor abort', async () => {
    let resolve!: (response: Response) => void;
    const delayed = new Promise<Response>((done) => { resolve = done; });
    const { session, fetcher } = setup((url) => {
      if (url.endsWith('/me')) return json(staff);
      if (url.endsWith('/logout')) return new Response(null, { status: 204 });
      return delayed;
    });
    await waitFor(() => expect(fetcher.mock.calls.some(([url]) => String(url).startsWith('/api/students'))).toBe(true));
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByLabelText('Password');
    await act(async () => { resolve(json(page)); });
    expect(screen.queryByText('Alex River')).not.toBeInTheDocument();
    expect(session.queryClient.getQueryCache().getAll()).toHaveLength(0);
  });
});

describe('staff directory', () => {
  it('keeps list 403 as an authorization error without pretending the session ended', async () => {
    setup((url) => url.endsWith('/me') ? json(staff) : json({}, 403));
    expect(await screen.findByRole('alert')).toHaveTextContent('Access is not available');
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeVisible();
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  });

  it('does not show the previous filter result while a new filter is loading', async () => {
    let resolve!: (response: Response) => void;
    const delayed = new Promise<Response>((done) => { resolve = done; });
    setup((url) => url.endsWith('/me') ? json(staff) : url.includes('activo=false') ? delayed : json(page));
    await screen.findByText('Alex River');
    fireEvent.change(screen.getByLabelText('Student state'), { target: { value: 'false' } });
    expect(await screen.findByText('Loading students…')).toBeVisible();
    expect(screen.queryByText('Alex River')).not.toBeInTheDocument();
    await act(async () => { resolve(json({ ...page, items: [], totalCount: 0 })); });
    expect(await screen.findByText('No students found')).toBeVisible();
  });

  it('sends server pagination and resets the page when search or state changes', async () => {
    const urls: string[] = [];
    setup((url) => {
      if (url.endsWith('/me')) return json(staff);
      urls.push(url);
      return json({ ...page, page: Number(new URL(url, 'https://localhost').searchParams.get('page')) });
    });
    await screen.findByText('Alex River');
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    await waitFor(() => expect(urls.at(-1)).toContain('page=2'));
    await screen.findByText('Alex River');
    fireEvent.change(screen.getByLabelText('Search students'), { target: { value: '  Alex  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(urls.at(-1)).toContain('search=Alex'));
    expect(urls.at(-1)).toContain('page=1');
    fireEvent.change(screen.getByLabelText('Student state'), { target: { value: 'false' } });
    await waitFor(() => expect(urls.at(-1)).toContain('activo=false'));
    expect(urls.at(-1)).toContain('page=1');
    expect(urls.every((url) => !url.includes('gymId'))).toBe(true);
  });

  it('shows a real empty result and recovers from a network failure through Retry', async () => {
    let fail = true;
    setup((url) => {
      if (url.endsWith('/me')) return json(staff);
      if (fail) throw new TypeError('internal network information');
      return json({ ...page, items: [], totalCount: 0 });
    });
    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load students');
    expect(screen.queryByText('internal network information')).not.toBeInTheDocument();
    fail = false;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('No students found')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
  });
});
