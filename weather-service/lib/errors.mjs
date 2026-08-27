/**
 * Errors deliberately carry a stable machine-readable code.  The renderer can
 * turn these into useful preparation messages without guessing from an HTTP
 * status or an upstream provider's prose response.
 */
export class WeatherServiceError extends Error {
  constructor(code, message, options = {}) {
    super(message);
    this.name = 'WeatherServiceError';
    this.code = code;
    this.status = options.status ?? statusForCode(code);
    this.retryable = options.retryable ?? false;
    this.details = options.details;
    this.cause = options.cause;
  }

  toJSON() {
    return {
      code: this.code,
      message: this.message,
      retryable: this.retryable,
      ...(this.details === undefined ? {} : { details: this.details }),
    };
  }
}

export function statusForCode(code) {
  switch (code) {
    case 'INVALID_REQUEST': return 400;
    case 'REQUEST_TOO_LARGE': return 413;
    case 'UNSUPPORTED_LOCATION': return 422;
    case 'UNKNOWN_JOB':
    case 'PACKAGE_NOT_FOUND':
    case 'CHUNK_NOT_FOUND': return 404;
    case 'JOB_NOT_READY':
    case 'JOB_CANCELLED':
    case 'BUILD_CANCELLED': return 409;
    case 'PROVIDER_CONFIGURATION': return 503;
    case 'PROVIDER_UNAVAILABLE': return 502;
    case 'PROVIDER_RESPONSE_INVALID': return 502;
    case 'PACKAGE_INTEGRITY': return 500;
    default: return 500;
  }
}

export function asWeatherServiceError(error) {
  if (error instanceof WeatherServiceError) return error;
  if (error?.name === 'AbortError') {
    return new WeatherServiceError('BUILD_CANCELLED', 'Weather package preparation was cancelled.', { status: 409 });
  }
  return new WeatherServiceError(
    'INTERNAL',
    error instanceof Error ? error.message : 'Weather package preparation failed.',
    { status: 500, cause: error },
  );
}

export function invariant(condition, code, message, options) {
  if (!condition) throw new WeatherServiceError(code, message, options);
}
