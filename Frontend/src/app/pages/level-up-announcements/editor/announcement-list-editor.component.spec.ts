import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { testI18n } from '../../../testing/i18n-testing';
import { AnnouncementListEditorComponent } from './announcement-list-editor.component';

describe('AnnouncementListEditorComponent', () => {
  let fixture: ComponentFixture<AnnouncementListEditorComponent>;
  let component: AnnouncementListEditorComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AnnouncementListEditorComponent, testI18n] });
    fixture = TestBed.createComponent(AnnouncementListEditorComponent);
    component = fixture.componentInstance;
    component.text = 'First\nSecond';
    component.value = component.text;
    component.groups = [];
    component.availableTokens = [{ name: 'level', requiresRewardRole: false, requiresSeason: false }];
    fixture.detectChanges();
  });

  it('recognizes a bulk-pasted list as separate messages', fakeAsync(() => {
    const groups: unknown[] = [];
    component.groupsChange.subscribe(value => groups.push(value));
    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = 'One\nTwo\nThree';
    textarea.dispatchEvent(new Event('input'));
    tick(200);
    expect((groups[0] as { messages: unknown[] }[])[0].messages).toHaveSize(3);
  }));

  it('reports the affected source line', fakeAsync(() => {
    component.onInput('// @group Broken\nMessage');
    tick(200);
    expect(component.errors[0].line).toBe(1);
    expect(component.errors[0].code).toBe('invalidGroupDirective');
  }));

  it('inserts a token at the current cursor position', fakeAsync(() => {
    const textarea = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = component.value;
    textarea.selectionStart = textarea.selectionEnd = 5;
    component.onCursor(textarea);
    component.insertToken(component.availableTokens[0], textarea);
    expect(component.value).toBe('First{level}\nSecond');
    tick(200);
    expect(textarea.selectionStart).toBe(12);
  }));
});
