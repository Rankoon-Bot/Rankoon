import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AppStore, Guild } from '../../store/app.store';
import { testI18n } from '../../testing/i18n-testing';
import { DashboardComponent } from './dashboard.component';

describe('DashboardComponent', () => {
  it('renders a visible backend load error with retry control', () => {
    const guild: Guild = { id: 'guild-1', name: 'Guild', icon: null, owner: true, permissions: '8', features: [], botInstalled: true, inviteUrl: '' };
    TestBed.configureTestingModule({ imports: [DashboardComponent, testI18n], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    TestBed.inject(AppStore).setSelectedGuild(guild);
    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne(`${environment.apiBaseUrl}/guilds/guild-1/dashboard`).flush({ message: 'Dashboard unavailable' }, { status: 500, statusText: 'Server Error' });
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
    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-1/dashboard`).flush({ guildName: 'Guild One' });

    store.setSelectedGuild(secondGuild);
    fixture.detectChanges();

    http.expectOne(`${environment.apiBaseUrl}/guilds/guild-2/dashboard`).flush({ guildName: 'Guild Two' });
    expect(fixture.componentInstance.data()?.guildName).toBe('Guild Two');
  });
});
