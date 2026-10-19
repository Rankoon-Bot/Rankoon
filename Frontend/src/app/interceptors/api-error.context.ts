import { HttpContextToken } from '@angular/common/http';

/** For requests whose caller deliberately handles an expected background/recovery state. */
export const SKIP_API_ERROR_TOAST = new HttpContextToken(() => false);
export const SUPPRESSED_API_ERROR_KEYS = new HttpContextToken<readonly string[]>(() => []);
