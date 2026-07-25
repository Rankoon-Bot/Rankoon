import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { DashboardComponent } from './dashboard.component';

const overview = (name: string) => ({ generatedAtUtc: '2026-01-01T00:00:00Z', period: 'SevenDays', periodStartUtc: '2025-12-25T00:00:00Z', periodEndUtc: '2026-01-01T00:00:00Z', guild: { guildId: 'guild-1', name, iconUrl: null, memberCount: 1, botCount: 0, liveVoiceMemberCount: 0 }, bot: { identityMode: 'Rankoon', displayName: 'Rankoon', avatarUrl: null, connected: true, status: 'Healthy', lastReadyAtUtc: null, statusReasonKey: null }, health: { overallStatus: 'Healthy', healthyModuleCount: 1, disabledModuleCount: 0, setupRequiredCount: 0, warningCount: 0, criticalCount: 0, unknownCount: 0, attentionItems: [] }, activity: { activeMemberCount: 0, xpAwarded: 0, voiceSeconds: 0, qualifiedActivityCount: 0, temporaryChannelsCreated: 0, sources: [], trend: [] }, modules: [], recentEvents: [] });

describe('DashboardComponent', () => {
  it('renders a visible backend load error with retry control', () => {
    const guild: Guild = { id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };
    TestBed.configureTestingModule({ imports: [DashboardComponent, testI18n], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    TestBed.inject(AppStore).setSelectedGuild(guild);
    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(`${environment.apiBaseUrl}/guilds/guild-1/dashboard?period=SevenDays`).flush({ message: 'Dashboard unavailable' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Dashboard unavailable');
    expect(alert.querySelector('button')).toBeTruthy();
  });

  it('reloads when the selected guild changes', () => {
    const firstGuild: Guild = { id: 'guild-1', name: 'Guild One', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };
    const secondGuild: Guild = { ...firstGuild, id: 'guild-2', name: 'Guild Two' };
    TestBed.configureTestingModule({ imports: [DashboardComponent, testI18n], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const store = TestBed.inject(AppStore);
    const http = TestBed.inject(HttpTestingController);
    store.setSelectedGuild(firstGuild);
    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/dashboard?period=SevenDays`).flush(overview('Guild One'));

    store.setSelectedGuild(secondGuild);
    fixture.detectChanges();

    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-2/dashboard?period=SevenDays`).flush(overview('Guild Two'));
    expect(fixture.componentInstance.data()?.guild.name).toBe('Guild Two');
  });
});
