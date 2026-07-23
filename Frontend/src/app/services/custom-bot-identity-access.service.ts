import { Injectable, computed, inject, signal } from '@angular/core';
import { GuildService } from './guild.service';

@Injectable({ providedIn: 'root' })
export class CustomBotIdentityAccessService {
  private readonly api = inject(GuildService);
  private readonly guildId = signal<string | null>(null);
  private readonly eligible = signal(false);
  readonly visible = computed(() => this.eligible());
  load(guildId: string): void {
    if (this.guildId() === guildId) return;
    this.guildId.set(guildId); this.eligible.set(false);
    this.api.customBotAccess(guildId).subscribe({ next: access => this.eligible.set(access.isEligible && (access.canActivate || access.hasReservation)), error: () => this.eligible.set(false) });
  }
  clear(): void { this.guildId.set(null); this.eligible.set(false); }
}
