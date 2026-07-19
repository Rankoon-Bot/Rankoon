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
});
