import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { testI18n } from '../../testing/i18n-testing';
import { AuthCallbackComponent } from './auth-callback.component';

describe('AuthCallbackComponent', () => {
  it('bootstraps the cookie session without passing callback credentials', () => {
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['handleSessionCallback', 'clearLocalAuth']);
    const router = jasmine.createSpyObj<Router>('Router', ['navigate', 'navigateByUrl']);
    auth.handleSessionCallback.and.returnValue(of(true));
    router.navigate.and.resolveTo(true);
    TestBed.configureTestingModule({
      imports: [AuthCallbackComponent, testI18n],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: { return_url: '/dashboard' } } } }
      ]
    });

    TestBed.createComponent(AuthCallbackComponent).detectChanges();
    expect(auth.handleSessionCallback).toHaveBeenCalledOnceWith();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('rejects an external callback return route', () => {
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['handleSessionCallback', 'clearLocalAuth']);
    const router = jasmine.createSpyObj<Router>('Router', ['navigate', 'navigateByUrl']);
    auth.handleSessionCallback.and.returnValue(of(true));
    router.navigate.and.resolveTo(true);
    router.navigateByUrl.and.resolveTo(true);
    TestBed.configureTestingModule({
      imports: [AuthCallbackComponent, testI18n],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: { return_url: '//attacker.example' } } } }
      ]
    });

    TestBed.createComponent(AuthCallbackComponent).detectChanges();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });
});
