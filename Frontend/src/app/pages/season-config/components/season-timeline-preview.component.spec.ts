import { ComponentFixture, TestBed } from '@angular/core/testing';
import { testI18n } from '../../../testing/i18n-testing';
import { SeasonTimelinePreviewComponent } from './season-timeline-preview.component';

describe('SeasonTimelinePreviewComponent', () => {
  let fixture: ComponentFixture<SeasonTimelinePreviewComponent>;
  let component: SeasonTimelinePreviewComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SeasonTimelinePreviewComponent, testI18n],
    });
    fixture = TestBed.createComponent(SeasonTimelinePreviewComponent);
    component = fixture.componentInstance;
  });

  it('renders each season and a gap only between adjacent seasons', () => {
    component.items = [
      {
        sequence: 1,
        name: 'Alpha',
        startsAtUtc: '2030-01-01T00:00:00.000Z',
        endsAtUtc: '2030-01-31T00:00:00.000Z',
      },
      {
        sequence: 2,
        name: 'Beta',
        startsAtUtc: '2030-02-02T00:00:00.000Z',
        endsAtUtc: '2030-03-04T00:00:00.000Z',
      },
    ];
    component.gapDays = 2;
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelectorAll('.season-segment').length).toBe(2);
    expect(element.querySelectorAll('.gap-segment').length).toBe(1);
    expect(element.textContent).toContain('Alpha');
    expect(element.textContent).toContain('Beta');
  });

  it('renders loading and unavailable states without timeline segments', () => {
    component.loading = true;
    fixture.detectChanges();
    let element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.rk-spinner')).not.toBeNull();
    expect(element.querySelector('.season-segment')).toBeNull();

    component.loading = false;
    component.unavailable = true;
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.timeline-state')).not.toBeNull();
    expect(element.querySelector('.rk-spinner')).toBeNull();
  });
});
