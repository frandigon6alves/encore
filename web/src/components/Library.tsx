import { useState } from 'react';
import { useLibrary, useRate, useRemove, useTracks } from '../queries';
import type { LibraryItem } from '../types';
import { Cover } from './Cover';
import { Stars } from './Stars';

export const formatLength = (ms: number | null) => {
  if (!ms) return '–';
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
};

export function Library({ onDiscover }: { onDiscover: () => void }) {
  const { data, isPending, error } = useLibrary();

  if (isPending) return <div className="grid">{Array.from({ length: 6 }, (_, i) => <div key={i} className="card skeleton" />)}</div>;
  if (error) return <p className="error">{error.message}</p>;
  if (data.length === 0)
    return (
      <div className="empty panel">
        <h2>Your shelf is empty</h2>
        <p className="muted">Search MusicBrainz and add the albums you love.</p>
        <button className="primary" onClick={onDiscover}>
          Discover albums
        </button>
      </div>
    );

  return (
    <>
      <h2 className="section-title">
        Library <span className="muted">{data.length} albums</span>
      </h2>
      <div className="grid">
        {data.map((entry) => (
          <AlbumCard key={entry.album.id} entry={entry} />
        ))}
      </div>
    </>
  );
}

function AlbumCard({ entry }: { entry: LibraryItem }) {
  const { album } = entry;
  const [open, setOpen] = useState(false);
  const rate = useRate();
  const remove = useRemove();
  const tracks = useTracks(album.id, open && album.status === 'ENRICHED');

  return (
    <article className="card">
      <Cover src={album.coverUrl} title={album.title} />
      <div className="card-body">
        <div className="card-head">
          <div>
            <h3>{album.title}</h3>
            <p className="muted">
              {album.artist?.name ?? 'Unknown'} {album.releaseYear && `· ${album.releaseYear}`}
            </p>
          </div>
          <StatusBadge status={album.status} />
        </div>
        <Stars
          label={`Rate ${album.title}`}
          value={entry.rating}
          onChange={(rating) => rate.mutate({ albumId: album.id, rating })}
        />
        {album.tags.length > 0 && (
          <ul className="tags">
            {album.tags.slice(0, 4).map((t) => (
              <li key={t}>{t}</li>
            ))}
          </ul>
        )}
        <div className="card-actions">
          {album.status === 'ENRICHED' && (
            <button className="ghost" aria-expanded={open} onClick={() => setOpen(!open)}>
              {open ? 'Hide' : 'Tracks'} · {formatLength(album.totalLengthMs)}
            </button>
          )}
          <button className="ghost danger" onClick={() => remove.mutate(album.id)} disabled={remove.isPending}>
            Remove
          </button>
        </div>
        {open && (
          <ol className="tracks">
            {tracks.data?.map((t) => (
              <li key={t.position}>
                <span>{t.title}</span>
                <span className="muted">{formatLength(t.lengthMs)}</span>
              </li>
            ))}
          </ol>
        )}
      </div>
    </article>
  );
}

function StatusBadge({ status }: { status: LibraryItem['album']['status'] }) {
  if (status === 'ENRICHED') return null;
  return (
    <span className={`badge badge-${status.toLowerCase()}`} title={status === 'PENDING' ? 'The worker is fetching tracks and tags' : 'MusicBrainz had no tracklist'}>
      {status === 'PENDING' ? 'Importing…' : 'Failed'}
    </span>
  );
}
