import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { testI18n } from '../../testing/i18n-testing';
import { AuthCallbackComponent } from './auth-callback.component';

describe('AuthCallbackComponent', () => {
  it('passes access and refresh callback tokens separately to AuthService', () => {
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['handleTokenCallback', 'clearLocalAuth']);
    const router = jasmine.createSpyObj<Router>('Router', ['navigate', 'navigateByUrl']);
    auth.handleTokenCallback.and.returnValue(of(true));
    router.navigate.and.resolveTo(true);
    TestBed.configureTestingModule({
      imports: [AuthCallbackComponent, testI18n],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: { token: 'access', refresh_token: 'refresh', return_url: '/dashboard' } } } }
      ]
    });

    TestBed.createComponent(AuthCallbackComponent).detectChanges();
    expect(auth.handleTokenCallback).toHaveBeenCalledOnceWith('access', 'refresh');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('rejects an external callback return route', () => {
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['handleTokenCallback', 'clearLocalAuth']);
    const router = jasmine.createSpyObj<Router>('Router', ['navigate', 'navigateByUrl']);
    auth.handleTokenCallback.and.returnValue(of(true));
    router.navigate.and.resolveTo(true);
    router.navigateByUrl.and.resolveTo(true);
    TestBed.configureTestingModule({
      imports: [AuthCallbackComponent, testI18n],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: { token: 'access', refresh_token: 'refresh', return_url: '//attacker.example' } } } }
      ]
    });

    TestBed.createComponent(AuthCallbackComponent).detectChanges();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });
});
