import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Stars } from './Stars';
import { formatLength } from './Library';

describe('Stars', () => {
  it('marks the current rating and reports clicks', () => {
    const onChange = vi.fn();
    render(<Stars label="Rate Blue" value={3} onChange={onChange} />);

    expect(screen.getByRole('radio', { name: '3 stars' })).toHaveAttribute('aria-checked', 'true');
    fireEvent.click(screen.getByRole('radio', { name: '5 stars' }));
    expect(onChange).toHaveBeenCalledWith(5);
  });

  it('is read-only without onChange', () => {
    render(<Stars label="Rating" value={null} />);
    expect(screen.getByRole('radio', { name: '1 star' })).toBeDisabled();
  });
});

describe('formatLength', () => {
  it.each([
    [null, '–'],
    [0, '–'],
    [61_000, '1:01'],
    [383_000, '6:23'],
  ])('%s -> %s', (ms, expected) => expect(formatLength(ms)).toBe(expected));
});
