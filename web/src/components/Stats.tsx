import { useStats } from '../queries';

export function Stats() {
  const { data, isPending, error } = useStats();
  if (isPending) return <p className="muted">Crunching numbers…</p>;
  if (error) return <p className="error">{error.message}</p>;

  const { totals, decades, topTags } = data;
  const maxDecade = Math.max(1, ...decades.map((d) => d.albums));

  return (
    <>
      <h2 className="section-title">Stats</h2>
      <div className="tiles">
        <Tile label="Albums" value={totals.albums} />
        <Tile label="Rated" value={totals.rated} />
        <Tile label="Average rating" value={totals.avgRating?.toFixed(2) ?? '–'} />
        <Tile label="Hours of music" value={(totals.totalMinutes / 60).toFixed(1)} />
      </div>
      <div className="two-col">
        <section className="panel">
          <h3>By decade</h3>
          {decades.length === 0 && <p className="muted">Add albums to see this.</p>}
          {decades.map((d) => (
            <div key={d.decade} className="bar-row">
              <span>{d.decade}s</span>
              <div className="bar-track">
                <div className="bar" style={{ width: `${(d.albums / maxDecade) * 100}%` }} />
              </div>
              <span className="muted">{d.albums}</span>
            </div>
          ))}
        </section>
        <section className="panel">
          <h3>Top tags</h3>
          {topTags.length === 0 && <p className="muted">Tags arrive once albums are imported.</p>}
          <ul className="tags big">
            {topTags.map((t) => (
              <li key={t.tag} title={t.avgRating ? `avg ★ ${t.avgRating}` : undefined}>
                {t.tag} <span className="muted">{t.albums}</span>
              </li>
            ))}
          </ul>
        </section>
      </div>
    </>
  );
}

function Tile({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="panel tile">
      <span className="muted">{label}</span>
      <strong>{value}</strong>
    </div>
  );
}
