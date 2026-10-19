import { ApiErrorBody, ApiValidationItem } from '../models/api-error.model';

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function readApiError(value: unknown): ApiErrorBody | null {
  if (!record(value)) return null;
  return {
    errorKey: typeof value['errorKey'] === 'string' ? value['errorKey'] : undefined,
    message: typeof value['message'] === 'string' ? value['message'] : undefined,
    parameters: record(value['parameters']) ? value['parameters'] : undefined,
    errors: record(value['errors']) ? value['errors'] as ApiErrorBody['errors'] : undefined,
  };
}

export function validationItems(body: ApiErrorBody | null): ApiValidationItem[] {
  return Object.entries(body?.errors ?? {}).flatMap(([field, items]) =>
    Array.isArray(items) ? (items as unknown[]).flatMap<ApiValidationItem>(item => {
      if (typeof item === 'string') return [{ field, message: item }];
      const value = readApiError(item);
      return value ? [{ field, errorKey: value.errorKey, message: value.message, parameters: value.parameters }] : [];
    }) : []
  );
}

export function statusErrorKey(status: number): string | undefined {
  switch (status) {
    case 0: return 'network.unavailable';
    case 400: return 'request.badRequest';
    case 401: return 'auth.unauthorized';
    case 403: return 'auth.forbidden';
    case 404: return 'resource.notFound';
    case 408: case 504: return 'request.timeout';
    case 409: return 'request.conflict';
    case 413: return 'request.tooLarge';
    case 429: return 'rateLimit.exceeded';
    case 502: case 503: return 'service.unavailable';
    default: return status >= 500 ? 'server.internal' : undefined;
  }
}
