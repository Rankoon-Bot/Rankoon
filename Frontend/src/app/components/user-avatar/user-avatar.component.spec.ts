import { ComponentFixture, TestBed } from '@angular/core/testing';
import { UserAvatarComponent } from './user-avatar.component';

describe('UserAvatarComponent', () => {
  let fixture: ComponentFixture<UserAvatarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [UserAvatarComponent] }).compileComponents();
    fixture = TestBed.createComponent(UserAvatarComponent);
    fixture.componentRef.setInput('displayName', 'Ada');
  });

  it('renders a decorative image when an icon URL is available', () => {
    fixture.componentRef.setInput('iconUrl', 'https://cdn.discordapp.com/avatar.png');
    fixture.detectChanges();

    const image = fixture.nativeElement.querySelector('img') as HTMLImageElement;
    expect(image.alt).toBe('');
    expect(image.loading).toBe('lazy');
  });

  it('uses the initial as fallback when the image is unavailable or fails', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('A');

    fixture.componentRef.setInput('iconUrl', 'https://cdn.discordapp.com/avatar.png');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('img').dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('A');
  });
});
