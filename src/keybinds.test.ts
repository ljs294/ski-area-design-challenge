import { describe, expect, it } from 'vitest';
import {
  DEFAULT_KEYBINDS,
  actionForKey,
  mergeKeybinds,
  normalizeKey,
} from './keybinds';

describe('mergeKeybinds', () => {
  it('fills in all defaults when given undefined', () => {
    expect(mergeKeybinds(undefined)).toEqual(DEFAULT_KEYBINDS);
  });

  it('fills in all defaults when given null', () => {
    expect(mergeKeybinds(null)).toEqual(DEFAULT_KEYBINDS);
  });

  it('fills in all defaults when given an empty object', () => {
    expect(mergeKeybinds({})).toEqual(DEFAULT_KEYBINDS);
  });

  it('overrides only the actions present in a partial stored object, normalizing case', () => {
    const merged = mergeKeybinds({ panForward: 'W', rotateLeft: 'Z' });
    expect(merged.panForward).toBe('w');
    expect(merged.rotateLeft).toBe('z');
    // Everything else stays default.
    expect(merged.panBackward).toBe(DEFAULT_KEYBINDS.panBackward);
    expect(merged.snapNorth).toBe(DEFAULT_KEYBINDS.snapNorth);
    expect(merged.openTrailsDashboard).toBe(DEFAULT_KEYBINDS.openTrailsDashboard);
  });

  it('ignores unknown keys in the stored object', () => {
    const merged = mergeKeybinds({ notARealAction: 'z', panForward: 'w' } as Partial<Record<string, string>>);
    expect(merged).toEqual(DEFAULT_KEYBINDS);
  });
});

describe('actionForKey', () => {
  it('finds the right action for a bound key', () => {
    expect(actionForKey(DEFAULT_KEYBINDS, 'w')).toBe('panForward');
    expect(actionForKey(DEFAULT_KEYBINDS, 'W')).toBe('panForward');
    expect(actionForKey(DEFAULT_KEYBINDS, '1')).toBe('openTrailsDashboard');
    expect(actionForKey(DEFAULT_KEYBINDS, 'r')).toBe('tiltUp');
    expect(actionForKey(DEFAULT_KEYBINDS, 'f')).toBe('tiltDown');
  });

  it('returns null for an unbound key', () => {
    expect(actionForKey(DEFAULT_KEYBINDS, 'z')).toBeNull();
  });
});

describe('normalizeKey', () => {
  it('lowercases single characters', () => {
    expect(normalizeKey('W')).toBe('w');
    expect(normalizeKey('1')).toBe('1');
  });

  it('passes through multi-character key names unchanged', () => {
    expect(normalizeKey('Escape')).toBe('Escape');
    expect(normalizeKey('ArrowUp')).toBe('ArrowUp');
  });
});
