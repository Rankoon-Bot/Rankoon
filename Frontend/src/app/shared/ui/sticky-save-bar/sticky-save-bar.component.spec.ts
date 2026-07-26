import { TestBed } from '@angular/core/testing';
import { testI18n } from '../../../testing/i18n-testing';
import { StickySaveBarComponent } from './sticky-save-bar.component';

describe('StickySaveBarComponent', () => {
  it('only renders for dirty state and prevents duplicate saves while busy', () => {
    const fixture = TestBed.configureTestingModule({ imports: [StickySaveBarComponent, testI18n] }).createComponent(StickySaveBarComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.rk-save-bar')).toBeNull();
    component.dirty = true;
    component.saving = true;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.rk-save-bar')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.rk-button--primary').disabled).toBeTrue();
  });

  it('handles Ctrl+S only for a valid unsaved draft', () => {
    const component = TestBed.configureTestingModule({ imports: [StickySaveBarComponent, testI18n] }).createComponent(StickySaveBarComponent).componentInstance;
    component.dirty = true;
    const save = spyOn(component.save, 'emit');
    component.onKeydown(new KeyboardEvent('keydown', { key: 's', ctrlKey: true }));
    expect(save).toHaveBeenCalledTimes(1);
    component.saving = true;
    component.onKeydown(new KeyboardEvent('keydown', { key: 's', metaKey: true }));
    expect(save).toHaveBeenCalledTimes(1);
  });
});
