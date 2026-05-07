// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Pipe, PipeTransform } from '@angular/core';

/**
 * P9.6 (rivoli-ai/andy-policies#88) — relative-time pipe for an override's
 * `expiresAt`. Impure (`pure: false`) so it re-renders on every change-
 * detection cycle; the parent component runs an interval that nudges CD
 * every 60s so the value advances even when there's no other interaction.
 *
 * Output formats:
 *   - "Expired" if the timestamp is in the past
 *   - "Xd Yh remaining" for ≥ 24h
 *   - "Xh Ym remaining" for ≥ 1h
 *   - "Xm remaining" for < 1h
 *   - "<1m remaining" inside the last 60 seconds
 *
 * Accepts an ISO-8601 string or a Date instance.
 */
@Pipe({ name: 'expiryCountdown', standalone: true, pure: false })
export class ExpiryCountdownPipe implements PipeTransform {
  transform(value: string | Date | null | undefined, now: Date = new Date()): string {
    if (!value) return '';
    const target = typeof value === 'string' ? new Date(value) : value;
    if (Number.isNaN(target.getTime())) return '';

    const ms = target.getTime() - now.getTime();
    if (ms <= 0) return 'Expired';

    const totalMinutes = Math.floor(ms / 60_000);
    const days = Math.floor(totalMinutes / (60 * 24));
    const hours = Math.floor((totalMinutes - days * 60 * 24) / 60);
    const minutes = totalMinutes - days * 60 * 24 - hours * 60;

    if (days > 0) return `${days}d ${hours}h remaining`;
    if (hours > 0) return `${hours}h ${minutes}m remaining`;
    if (minutes > 0) return `${minutes}m remaining`;
    return '<1m remaining';
  }
}
