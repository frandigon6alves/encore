import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, gql } from './api';
import type { AlbumSummary, LibraryItem, LibraryStats, Recommendation, Track } from './types';

const LIBRARY = `
  query MyLibrary {
    myLibrary {
      rating review addedAt
      album { id mbId title releaseYear coverUrl status totalLengthMs tags artist { name } }
    }
  }`;

const TRACKS = `
  query AlbumTracks($id: UUID!) {
    album(id: $id) { tracks { position title lengthMs } }
  }`;

export function useLibrary() {
  return useQuery({
    queryKey: ['library'],
    queryFn: () => gql<{ myLibrary: LibraryItem[] }>(LIBRARY).then((d) => d.myLibrary),
    // Albums are enriched asynchronously by the worker: poll only while something is pending.
    refetchInterval: (query) => (query.state.data?.some((e) => e.album.status === 'PENDING') ? 2000 : false),
  });
}

export function useTracks(albumId: string, enabled: boolean) {
  return useQuery({
    queryKey: ['tracks', albumId],
    queryFn: () => gql<{ album: { tracks: Track[] } | null }>(TRACKS, { id: albumId }).then((d) => d.album?.tracks ?? []),
    enabled,
    staleTime: Infinity,
  });
}

export function useSearch(q: string) {
  return useQuery({
    queryKey: ['search', q],
    queryFn: ({ signal }) => api<AlbumSummary[]>(`/api/v1/search/albums?q=${encodeURIComponent(q)}`, { signal }),
    enabled: q.length >= 2,
    staleTime: 5 * 60_000,
  });
}

export function useStats() {
  return useQuery({ queryKey: ['stats'], queryFn: () => api<LibraryStats>('/api/v1/stats') });
}

export function useRecommendations() {
  return useQuery({
    queryKey: ['recommendations'],
    queryFn: () => api<Recommendation[]>('/api/v1/recommendations?limit=12'),
    retry: false, // a 503 here is an expected, handled state
  });
}

function useLibraryMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () =>
      Promise.all(['library', 'stats', 'recommendations'].map((key) => client.invalidateQueries({ queryKey: [key] }))),
  });
}

export const useAddAlbum = () =>
  useLibraryMutation((mbId: string) => api('/api/v1/library', { method: 'POST', body: JSON.stringify({ mbId }) }));

export const useRate = () =>
  useLibraryMutation(({ albumId, rating }: { albumId: string; rating: number }) =>
    api(`/api/v1/library/${albumId}/rating`, { method: 'PUT', body: JSON.stringify({ rating }) }),
  );

export const useRemove = () =>
  useLibraryMutation((albumId: string) => api(`/api/v1/library/${albumId}`, { method: 'DELETE' }));
