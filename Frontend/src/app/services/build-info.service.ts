import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';
import { environment } from '../../environments/environment';

interface BuildInfo {
  buildVersion: string;
}

function shortenBuildVersion(version: string): string {
  return version.replace(/\+[0-9a-f]+([0-9a-f]{8})$/i, '+$1');
}

@Injectable({ providedIn: 'root' })
export class BuildInfoService {
  private readonly http = inject(HttpClient);
  readonly buildVersion = signal(shortenBuildVersion(environment.buildVersion));

  constructor() {
    this.http.get<BuildInfo>(`${environment.apiBaseUrl}/info`).pipe(
      catchError(() => of(null))
    ).subscribe((info) => {
      if (info?.buildVersion) this.buildVersion.set(shortenBuildVersion(info.buildVersion));
    });
  }
}
