import { ResolveFn } from '@angular/router';
import { inject } from '@angular/core';
import {
  ModuleTranslationService,
  TranslationScope,
} from './module-translation.service';

export const translationScope =
  (scope: TranslationScope): ResolveFn<void> =>
  () =>
    inject(ModuleTranslationService).load(scope);
