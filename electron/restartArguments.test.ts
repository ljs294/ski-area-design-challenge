import { describe, expect, it } from 'vitest';
import { restartArguments, resumeSaveArgument } from './restartArguments';

describe('restart arguments', () => {
  it('preserves the development entrypoint and replaces the prior mountain', () => {
    const args = restartArguments(['.', '--inspect=9229', '--mountain-resume-save=old'], 'save:New Mountain');
    expect(args).toEqual(['.', '--inspect=9229', '--mountain-resume-save=save:New Mountain']);
    expect(resumeSaveArgument(args)).toBe('save:New Mountain');
  });
  it('supports packaged launches and ordinary startup', () => {
    expect(resumeSaveArgument(restartArguments([], 'mountain'))).toBe('mountain');
    expect(resumeSaveArgument([])).toBeUndefined();
  });
});
