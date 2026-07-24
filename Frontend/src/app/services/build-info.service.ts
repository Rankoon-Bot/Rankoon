import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';
import { environment } from '../../environments/environment';

interface BuildInfo {
  buildVersion: string;
}

@Injectable({ providedIn: 'root' })
export class BuildInfoService {
  private readonly http = inject(HttpClient);
  readonly buildVersion = signal(environment.buildVersion);

  constructor() {
    this.http.get<BuildInfo>(`${environment.apiBaseUrl}/info`).pipe(
      catchError(() => of(null))
    ).subscribe((info) => {
      if (info?.buildVersion) this.buildVersion.set(info.buildVersion);
    });
  }
}
