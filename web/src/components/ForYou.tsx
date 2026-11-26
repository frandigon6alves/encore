import { ApiError } from '../api';
import { useRecommendations } from '../queries';
import { Cover } from './Cover';

export function ForYou() {
  const { data, isPending, error, refetch } = useRecommendations();

  return (
    <>
      <h2 className="section-title">For you</h2>
      <p className="muted">Albums other listeners added that share tags with the ones you rated 4★ or more.</p>
      {isPending && <p className="muted">Asking the recommender…</p>}
      {error && (
        <div className="panel">
          <p className="error">
            {error instanceof ApiError && error.status === 503
              ? 'The recommender service is down. The rest of Encore still works.'
              : error.message}
          </p>
          <button className="ghost" onClick={() => refetch()}>
            Try again
          </button>
        </div>
      )}
      {data?.length === 0 && <p className="muted">Rate a few albums 4★ or 5★ first.</p>}
      <div className="grid">
        {data?.map((r) => (
          <article key={r.albumId} className="card">
            <Cover src={r.coverUrl} title={r.title} />
            <div className="card-body">
              <h3>{r.title}</h3>
              <p className="muted">{r.artist}</p>
              <ul className="tags">
                {r.matchedTags.map((t) => (
                  <li key={t}>{t}</li>
                ))}
              </ul>
            </div>
          </article>
        ))}
      </div>
    </>
  );
}
