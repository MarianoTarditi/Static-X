export class ApiError extends Error {
  readonly status: number;
  constructor(status: number) {
    super('The request could not be completed.');
    this.status = status;
  }
}

export async function request(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (!headers.has('Accept')) headers.set('Accept', 'application/json');
  const response = await fetch(`/api${path}`, {
    ...init,
    credentials: 'same-origin',
    cache: 'no-store',
    redirect: 'error',
    headers,
  });
  // Deliberately do not consume or display server error bodies.
  if (!response.ok) throw new ApiError(response.status);
  return response;
}

export function safeError(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    if (error.status === 403) return 'Access is not available for this account. Contact your gym administrator.';
    if (error.status === 429) return 'Too many requests. Please wait a moment and try again.';
    if (error.status === 400) return 'The request could not be accepted. Check your details and try again.';
  }
  return fallback;
}
