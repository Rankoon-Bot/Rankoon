# API errors and frontend notifications

API failures use the existing canonical response: errorKey, safe message, traceId,
and optional parameters and field errors. Local domain validation remains in
controllers. The global exception handler maps unhandled failures through
Backend/Api/ApiExceptionMapper.cs and retains the original exception in logging
and operational incident recording. Exception messages, tokens and stack traces
are never copied into the public response.

## Classification and recovery

The mapper inspects inner exceptions and aggregate causes, including Discord
exceptions wrapped by services. Stable Discord codes distinguish inaccessible
resources, missing permissions, unavailable resources, platform limits, invalid
requests, authentication failures, throttling and outages. HTTP status is the
fallback for unknown Discord codes. Network and MongoDB connection failures map
to service.unavailable; timeouts map to request.timeout. Unknown causes retain
the safe server.internal fallback. Classification does not match exception text.

Discord permission errors return 422: these describe the bot's Discord access,
whereas 401/403 describe the caller's Rankoon authentication/authorization.
This also prevents the browser from attempting CSRF recovery for a Discord
permission failure. Missing Discord resources return 409; upstream outages and
rate limits return 502/503; timeouts return 504. A cancelled client request is
not treated as a server incident. The hub delete operation still retains its
stored configuration if Discord deletion fails.

Messages explain the next step: check channel/category access, bot permissions
and role hierarchy, run bot diagnostics, or verify the current state before
retrying after an ambiguous timeout. The backend provides an English fallback;
all six frontend locales contain the stable error keys.

## Browser behavior

apiErrorInterceptor wraps authInterceptor, so recovered authentication/CSRF
attempts stay quiet. Final failures of /api/ requests produce a toast even if a
page only has inline error handling. Non-JSON proxy responses and network failures
use translated status fallbacks. The original error is rethrown so callers can
retain drafts, field validation and conflict handling. No new automatic retries
are introduced.

Pages needing both a notification and local handling should call
ApiErrorService.notify(error, fallbackKey). It returns the same resolved field
errors as resolve(), but deduplicates the interceptor/page notification for the
same response object. resolve() itself has no notification side effects. Active
identical error toasts are coalesced, and a new failure can notify again after
the previous toast is dismissed or expires.

Expected anonymous session checks (401/403) and internal refresh/CSRF requests
stay quiet. For an expected domain result, set SUPPRESSED_API_ERROR_KEYS on the
request HttpContext and list only the expected keys: missing diagnostic history
and platform-bot installation polling use this mechanism. Other errors on those
requests remain visible. SKIP_API_ERROR_TOAST is available for callers that
explicitly own the complete notification flow; use it sparingly.

## Verification

ApiExceptionMapperTests cover wrapped Discord errors, status fallback, unknown
causes, cancellation and safe serialization. ApiExceptionPipelineTests exercise
the real exception middleware with a test-only controller and incident recorder.
ApiErrorTranslationParityTests check every backend key in all six locales.
Browser unit tests cover recovery ordering, opt-outs, malformed responses,
network/gateway failures and notification deduplication.
