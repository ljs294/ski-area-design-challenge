const RESUME_ARGUMENT = '--mountain-resume-save=';

export function restartArguments(args: readonly string[], saveKey: string): string[] {
  return [...args.filter((arg) => !arg.startsWith(RESUME_ARGUMENT)), `${RESUME_ARGUMENT}${saveKey}`];
}

export function resumeSaveArgument(args: readonly string[]): string | undefined {
  return args.find((arg) => arg.startsWith(RESUME_ARGUMENT))?.slice(RESUME_ARGUMENT.length) || undefined;
}
