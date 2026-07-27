import { Injectable, inject } from '@angular/core';
import { Router, UrlTree } from '@angular/router';
import { Observable, of } from 'rxjs';
import { finalize, shareReplay, tap } from 'rxjs/operators';
import { GuildCapabilities } from '../models/guild-permissions.models';
import { AppStore, Guild } from '../store/app.store';
import { GuildService } from './guild.service';

@Injectable({ providedIn: 'root' })
export class GuildAccessService {
  private readonly CAPABILITIES_CACHE_MS = 60_000;
  private readonly appStore = inject(AppStore);
  private readonly guildService = inject(GuildService);
  private readonly router = inject(Router);
  private readonly capabilitiesCache = new Map<string, { value: GuildCapabilities; expiresAt: number }>();
  private readonly capabilitiesRequests = new Map<string, Observable<GuildCapabilities>>();

  loadCapabilities(guildId: string, refresh = false): Observable<GuildCapabilities> {
    const cached = this.capabilitiesCache.get(guildId);
    if (!refresh && cached && cached.expiresAt > Date.now()) return of(cached.value);

    const inFlight = this.capabilitiesRequests.get(guildId);
    if (inFlight) return inFlight;

    const request = this.guildService.capabilities(guildId).pipe(
      tap(capabilities => {
        this.capabilitiesCache.set(guildId, { value: capabilities, expiresAt: Date.now() + this.CAPABILITIES_CACHE_MS });
        this.appStore.setGuildCapabilities(capabilities);
      }),
      finalize(() => this.capabilitiesRequests.delete(guildId)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.capabilitiesRequests.set(guildId, request);
    return request;
  }

  selectAndNavigate(guild: Guild): Observable<GuildCapabilities> {
    this.clearCache();
    this.appStore.setSelectedGuild(guild);
    return this.loadCapabilities(guild.id, true).pipe(
      tap(capabilities => {
        if (this.appStore.selectedGuild()?.id !== guild.id) return;
        void this.router.navigateByUrl(this.destination(capabilities));
      })
    );
  }

  destination(capabilities: GuildCapabilities): UrlTree {
    if (capabilities.canAccessSettings) return this.router.createUrlTree(['/dashboard']);
    return this.router.createUrlTree(['/rankings', capabilities.leaderboardAlias]);
  }

  clearCache(guildId?: string): void {
    if (guildId) {
      this.capabilitiesCache.delete(guildId);
      this.guildService.invalidateResourceCache(guildId);
      return;
    }
    this.capabilitiesCache.clear();
    this.guildService.invalidateResourceCache();
  }
}
