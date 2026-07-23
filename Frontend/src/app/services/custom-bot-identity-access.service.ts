import { Injectable, computed, inject, signal } from '@angular/core';
import { GuildService } from './guild.service';
import { Subscription } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class CustomBotIdentityAccessService {
  private readonly api = inject(GuildService);
  private readonly guildId = signal<string | null>(null);
  private readonly eligible = signal(false);
  private request?: Subscription;
  readonly visible = computed(() => this.eligible());
  load(guildId: string): void {
    this.guildId.set(guildId); this.eligible.set(false);
    this.request?.unsubscribe();
    this.request = this.api.customBotAccess(guildId).subscribe({ next: access => { if (this.guildId() === guildId) this.eligible.set(access.isEligible && (access.canActivate || access.hasReservation || access.hasConfiguredIdentity)); }, error: () => { if (this.guildId() === guildId) this.eligible.set(false); } });
  }
  clear(): void { this.request?.unsubscribe(); this.guildId.set(null); this.eligible.set(false); }
}
