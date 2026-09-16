import type { Page } from '@playwright/test';

export interface DiagnosticArtifactRecord {
  role: string;
  availability: 'available' | 'unavailable';
  path?: string;
  bytes?: number;
  sha256?: string;
  reason?: string;
}

export function startIntegratedDiagnosticProfile(page: Page, options: {
  outputDir: string;
  runId: string;
}): Promise<() => Promise<DiagnosticArtifactRecord[]>>;

export function orderedReactProfileEntries(ring: unknown): unknown[];
export function diagnosticArtifactErrors(records: readonly DiagnosticArtifactRecord[]): string[];
