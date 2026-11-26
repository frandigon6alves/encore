// Thin HTTP layer: auth header, correlation id, RFC 9457 problem details -> ApiError.

const TOKEN_KEY = 'encore.token';

export const auth = {
  get token(): string | null {
    try {
      return localStorage.getItem(TOKEN_KEY);
    } catch {
      return null;
    }
  },
  set(token: string | null) {
    try {
      if (token) localStorage.setItem(TOKEN_KEY, token);
      else localStorage.removeItem(TOKEN_KEY);
    } catch {
      /* storage unavailable: session-only login */
    }
    window.dispatchEvent(new Event('encore:auth'));
  },
};

export interface Problem {
  title?: string;
  detail?: string;
  status?: number;
  traceId?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: Problem = {},
  ) {
    const fieldErrors = problem.errors ? Object.values(problem.errors).flat().join(' ') : '';
    super(fieldErrors || problem.detail || problem.title || `Request failed (${status})`);
  }
}

async function send(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  headers.set('X-Correlation-Id', crypto.randomUUID()); // shows up in API logs and traces
  if (init.body) headers.set('Content-Type', 'application/json');
  if (auth.token) headers.set('Authorization', `Bearer ${auth.token}`);

  const response = await fetch(path, { ...init, headers });
  if (response.status === 401) auth.set(null);
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new ApiError(response.status, problem);
  }
  return response;
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await send(path, init);
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export async function gql<T>(query: string, variables?: Record<string, unknown>): Promise<T> {
  const body = await api<{ data?: T; errors?: { message: string }[] }>('/graphql', {
    method: 'POST',
    body: JSON.stringify({ query, variables }),
  });
  if (body.errors?.length) throw new ApiError(200, { title: body.errors.map((e) => e.message).join('; ') });
  return body.data as T;
}
