export interface PackagedElectronControl {
  pid: number | undefined;
  endpoint: string;
  version: unknown;
  pageTargets: unknown[];
  args: string[];
  readonly exitCode: number | null;
  readonly stdout: string;
  readonly stderr: string;
  close(options?: { graceMs?: number }): Promise<{ forced: boolean; exitCode: number | null }>;
  exited: Promise<{ exitCode: number | null; signal: string | null }>;
}

export function launchPackagedElectron(options: {
  executablePath: string;
  userDataDir: string;
  scenario: unknown;
  telemetryEnabled: boolean;
  devConsole: boolean;
  deviceScaleFactor: number;
}): Promise<PackagedElectronControl>;

export function validateDebugPort(value: unknown): number | undefined;
export function reserveLoopbackPort(requested?: number): Promise<number>;
export function cleanupPackagedElectronFromUserData(userDataDir: string, expectedExecutable: string): Promise<void>;
export function cdpPageTargets(value: unknown): unknown[];
export function packagedElectronArguments(options: Record<string, unknown>): string[];
