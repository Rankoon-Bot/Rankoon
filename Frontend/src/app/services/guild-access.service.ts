import { Injectable, inject } from '@angular/core';
import { Router, UrlTree } from '@angular/router';
import { Observable, of, tap } from 'rxjs';
import { GuildCapabilities } from '../models/guild-permissions.models';
import { AppStore, Guild } from '../store/app.store';
import { GuildService } from './guild.service';

@Injectable({ providedIn: 'root' })
export class GuildAccessService {
  private readonly appStore = inject(AppStore);
  private readonly guildService = inject(GuildService);
  private readonly router = inject(Router);

  loadCapabilities(guildId: string, refresh = false): Observable<GuildCapabilities> {
    const current = this.appStore.guildCapabilities();
    if (!refresh && current?.guildId === guildId) return of(current);

    return this.guildService.capabilities(guildId).pipe(
      tap(capabilities => this.appStore.setGuildCapabilities(capabilities))
    );
  }

  selectAndNavigate(guild: Guild): Observable<GuildCapabilities> {
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
}
