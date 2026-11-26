import { useState, type FormEvent } from 'react';
import { ApiError, api, auth } from '../api';

export function Login() {
  const [username, setUsername] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { accessToken } = await api<{ accessToken: string }>('/api/v1/auth/token', {
        method: 'POST',
        body: JSON.stringify({ username: username.trim().toLowerCase() }),
      });
      auth.set(accessToken);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not reach the API.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login">
      <form className="panel login-card" onSubmit={submit}>
        <h1>
          <span className="brand-disc" aria-hidden /> Encore
        </h1>
        <p className="muted">Your album diary. Rate what you hear, find what to hear next.</p>
        <label htmlFor="username">Username</label>
        <input
          id="username"
          autoFocus
          autoComplete="username"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          placeholder="e.g. fran"
        />
        {error && <p className="error" role="alert">{error}</p>}
        <button className="primary" disabled={busy || username.trim().length < 3}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
        <p className="hint">Demo auth: any name with 3+ letters gets a JWT.</p>
      </form>
    </main>
  );
}
