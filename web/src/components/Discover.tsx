import { useDeferredValue, useState } from 'react';
import { ApiError } from '../api';
import { useAddAlbum, useLibrary, useSearch } from '../queries';
import { Cover } from './Cover';

export function Discover() {
  const [text, setText] = useState('');
  const q = useDeferredValue(text.trim());
  const search = useSearch(q);
  const add = useAddAlbum();
  const library = useLibrary();
  const owned = new Set(library.data?.map((e) => e.album.mbId));

  return (
    <>
      <h2 className="section-title">Discover</h2>
      <input
        className="search"
        type="search"
        placeholder="Search albums on MusicBrainz — try “Kind of Blue”"
        value={text}
        onChange={(e) => setText(e.target.value)}
        aria-label="Search albums"
      />
      {search.error && (
        <p className="error">{search.error instanceof ApiError && search.error.status === 429 ? 'Slow down: 20 searches per minute.' : search.error.message}</p>
      )}
      {add.error && <p className="error">{add.error.message}</p>}
      {search.isFetching && <p className="muted">Searching…</p>}
      <ul className="results">
        {search.data?.map((album) => {
          const inLibrary = owned.has(album.mbId);
          return (
            <li key={album.mbId} className="result">
              <Cover src={album.coverUrl} title={album.title} />
              <div>
                <strong>{album.title}</strong>
                <p className="muted">
                  {album.artistName} {album.releaseYear && `· ${album.releaseYear}`}
                </p>
              </div>
              <button
                className={inLibrary ? 'ghost' : 'primary'}
                disabled={inLibrary || (add.isPending && add.variables === album.mbId)}
                onClick={() => add.mutate(album.mbId)}
              >
                {inLibrary ? 'In library' : 'Add'}
              </button>
            </li>
          );
        })}
      </ul>
      {search.data?.length === 0 && <p className="muted">No albums found.</p>}
    </>
  );
}
