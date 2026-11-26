export interface AlbumSummary {
  mbId: string;
  title: string;
  artistName: string;
  releaseYear: number | null;
  coverUrl: string;
}

// GraphQL enums are UPPER_CASE; REST sends the C# names.
export type GqlStatus = 'PENDING' | 'ENRICHED' | 'FAILED';

export interface LibraryItem {
  rating: number | null;
  review: string | null;
  addedAt: string;
  album: {
    id: string;
    mbId: string;
    title: string;
    releaseYear: number | null;
    coverUrl: string | null;
    status: GqlStatus;
    totalLengthMs: number;
    tags: string[];
    artist: { name: string } | null;
  };
}

export interface Track {
  position: number;
  title: string;
  lengthMs: number | null;
}

export interface LibraryStats {
  totals: { albums: number; rated: number; avgRating: number | null; totalMinutes: number };
  decades: { decade: number; albums: number; avgRating: number | null }[];
  topTags: { tag: string; albums: number; avgRating: number | null }[];
}

export interface Recommendation {
  albumId: string;
  title: string;
  artist: string;
  coverUrl: string;
  score: number;
  matchedTags: string[];
}
