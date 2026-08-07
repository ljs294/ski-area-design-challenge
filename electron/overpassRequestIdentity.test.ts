import type { WebRequest } from 'electron';
import { describe, expect, it, vi } from 'vitest';
import { OVERPASS_ENDPOINTS, OVERPASS_REPOSITORY_URL } from '../src/overpassConfig';
import {
  buildOverpassUserAgent,
  registerOverpassRequestIdentity,
} from './overpassRequestIdentity';

type HeaderCallback = (response: {
  requestHeaders?: Record<string, string | string[]>;
}) => void;

type HeaderListener = (
  details: { requestHeaders: Record<string, string> },
  callback: HeaderCallback,
) => void;

describe('Overpass Electron request identity', () => {
  it('registers one listener filtered to the exact configured endpoints', () => {
    const onBeforeSendHeaders = vi.fn();

    registerOverpassRequestIdentity(
      { onBeforeSendHeaders } as unknown as WebRequest,
      '1.2.3',
    );

    expect(onBeforeSendHeaders).toHaveBeenCalledOnce();
    expect(onBeforeSendHeaders.mock.calls[0]?.[0]).toEqual({
      urls: [...OVERPASS_ENDPOINTS],
    });
    expect(buildOverpassUserAgent('1.2.3')).toBe(
      `Mountain-Planner/1.2.3 (+${OVERPASS_REPOSITORY_URL})`,
    );
  });

  it('replaces User-Agent, preserves unrelated headers, and completes once', () => {
    const onBeforeSendHeaders = vi.fn();
    registerOverpassRequestIdentity(
      { onBeforeSendHeaders } as unknown as WebRequest,
      '9.8.7',
    );
    const listener = onBeforeSendHeaders.mock.calls[0]?.[1] as HeaderListener;
    const callback = vi.fn<HeaderCallback>();

    listener({
      requestHeaders: {
        Accept: 'application/json',
        'user-agent': 'Electron/43 Chrome/142',
        'X-Request-Token': 'unchanged',
      },
    }, callback);

    expect(callback).toHaveBeenCalledOnce();
    expect(callback).toHaveBeenCalledWith({
      requestHeaders: {
        Accept: 'application/json',
        'X-Request-Token': 'unchanged',
        'User-Agent': `Mountain-Planner/9.8.7 (+${OVERPASS_REPOSITORY_URL})`,
      },
    });
  });
});
