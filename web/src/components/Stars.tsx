interface StarsProps {
  value: number | null;
  onChange?: (rating: number) => void;
  label: string;
}

/** Star rating as a radio group: keyboard and screen-reader friendly. */
export function Stars({ value, onChange, label }: StarsProps) {
  return (
    <div className="stars" role="radiogroup" aria-label={label}>
      {[1, 2, 3, 4, 5].map((n) => (
        <button
          key={n}
          type="button"
          role="radio"
          aria-checked={value === n}
          aria-label={`${n} star${n > 1 ? 's' : ''}`}
          className={value !== null && n <= value ? 'star on' : 'star'}
          disabled={!onChange}
          onClick={() => onChange?.(n)}
        >
          ★
        </button>
      ))}
    </div>
  );
}
