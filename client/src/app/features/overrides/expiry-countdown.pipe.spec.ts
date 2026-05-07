// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ExpiryCountdownPipe } from './expiry-countdown.pipe';

describe('ExpiryCountdownPipe (P9.6)', () => {
  let pipe: ExpiryCountdownPipe;
  // Fixed reference instant so tests don't drift with wall clock.
  const now = new Date('2026-05-07T12:00:00Z');

  beforeEach(() => {
    pipe = new ExpiryCountdownPipe();
  });

  it('returns "Expired" for a past timestamp', () => {
    const past = new Date(now.getTime() - 60_000).toISOString();
    expect(pipe.transform(past, now)).toBe('Expired');
  });

  it('returns "Expired" exactly at the boundary', () => {
    expect(pipe.transform(now.toISOString(), now)).toBe('Expired');
  });

  it('formats sub-hour times in minutes', () => {
    const future = new Date(now.getTime() + 5 * 60_000).toISOString();
    expect(pipe.transform(future, now)).toBe('5m remaining');
  });

  it('shows <1m for the last sub-minute', () => {
    const future = new Date(now.getTime() + 30_000).toISOString();
    expect(pipe.transform(future, now)).toBe('<1m remaining');
  });

  it('formats hour-bucket times as "Xh Ym remaining"', () => {
    const future = new Date(now.getTime() + (3 * 3600_000) + (15 * 60_000)).toISOString();
    expect(pipe.transform(future, now)).toBe('3h 15m remaining');
  });

  it('formats multi-day times as "Xd Yh remaining"', () => {
    // 2 days, 6 hours
    const future = new Date(now.getTime() + ((2 * 24 + 6) * 3600_000)).toISOString();
    expect(pipe.transform(future, now)).toBe('2d 6h remaining');
  });

  it('returns empty string for null/undefined/invalid input', () => {
    expect(pipe.transform(null, now)).toBe('');
    expect(pipe.transform(undefined, now)).toBe('');
    expect(pipe.transform('not-a-date', now)).toBe('');
  });

  it('accepts a Date instance', () => {
    expect(pipe.transform(new Date(now.getTime() + 90 * 60_000), now)).toBe('1h 30m remaining');
  });
});
