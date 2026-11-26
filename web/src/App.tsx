import { useState, useSyncExternalStore } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { auth } from './api';
import { Login } from './components/Login';
import { Library } from './components/Library';
import { Discover } from './components/Discover';
import { Stats } from './components/Stats';
import { ForYou } from './components/ForYou';

const TABS = { library: 'Library', discover: 'Discover', stats: 'Stats', forYou: 'For you' } as const;
type Tab = keyof typeof TABS;

const subscribe = (notify: () => void) => {
  window.addEventListener('encore:auth', notify);
  return () => window.removeEventListener('encore:auth', notify);
};

function usernameFromToken(token: string): string {
  try {
    return JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))).sub ?? '';
  } catch {
    return '';
  }
}

export function App() {
  const token = useSyncExternalStore(subscribe, () => auth.token, () => null);
  const [tab, setTab] = useState<Tab>('library');
  const queryClient = useQueryClient();

  if (!token) return <Login />;

  return (
    <div className="shell">
      <header className="topbar">
        <span className="brand">
          <span className="brand-disc" aria-hidden /> Encore
        </span>
        <nav className="tabs" aria-label="Sections">
          {(Object.keys(TABS) as Tab[]).map((key) => (
            <button key={key} className="tab" aria-current={tab === key ? 'page' : undefined} onClick={() => setTab(key)}>
              {TABS[key]}
            </button>
          ))}
        </nav>
        <button
          className="ghost"
          onClick={() => {
            auth.set(null);
            queryClient.clear();
          }}
        >
          {usernameFromToken(token)} · Sign out
        </button>
      </header>
      <main className="content">
        {tab === 'library' && <Library onDiscover={() => setTab('discover')} />}
        {tab === 'discover' && <Discover />}
        {tab === 'stats' && <Stats />}
        {tab === 'forYou' && <ForYou />}
      </main>
    </div>
  );
}
