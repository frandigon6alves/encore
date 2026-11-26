import { useState } from 'react';

/** Cover Art Archive has no art for many albums: fall back to a generated vinyl. */
export function Cover({ src, title }: { src: string | null; title: string }) {
  const [failed, setFailed] = useState(false);
  if (!src || failed) {
    const hue = [...title].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 7);
    return <div className="cover cover-fallback" style={{ ['--hue' as string]: hue }} aria-hidden />;
  }
  return <img className="cover" src={src} alt="" loading="lazy" onError={() => setFailed(true)} />;
}
