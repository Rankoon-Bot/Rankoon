import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ToastService } from '../../services/toast.service';
import { testI18n } from '../../testing/i18n-testing';
import { ToastContainerComponent } from './toast-container.component';

describe('ToastContainerComponent', () => {
  let fixture: ComponentFixture<ToastContainerComponent>;
  let toasts: ToastService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ToastContainerComponent, testI18n],
    }).compileComponents();
    toasts = TestBed.inject(ToastService);
    fixture = TestBed.createComponent(ToastContainerComponent);
  });

  it('renders a toast and dismisses it from the close button', () => {
    toasts.success('Saved');
    fixture.detectChanges();

    const toast = fixture.nativeElement.querySelector('.toast') as HTMLElement;
    expect(toast.textContent).toContain('Saved');
    (fixture.nativeElement.querySelector('.toast__close') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.toast')).toBeNull();
  });
});
