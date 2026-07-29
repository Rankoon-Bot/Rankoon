import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Season } from '../../../services/guild.service';
import { testI18n } from '../../../testing/i18n-testing';
import { SeasonInstanceListComponent } from './season-instance-list.component';

describe('SeasonInstanceListComponent', () => {
  let fixture: ComponentFixture<SeasonInstanceListComponent>;
  let component: SeasonInstanceListComponent;
  const season = (sequence: number, status: Season['status'], previousSeasonId: string | null = null): Season => ({
    id: `season-${sequence}`, sequence, name: `Season ${sequence}`, description: null, status,
    startsAtUtc: '2030-01-01T00:00:00.000Z', endsAtUtc: '2030-02-01T00:00:00.000Z', previousSeasonId,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SeasonInstanceListComponent, testI18n] });
    fixture = TestBed.createComponent(SeasonInstanceListComponent);
    component = fixture.componentInstance;
  });

  it('requires cancellation before an individual season can be deleted', () => {
    expect(component.actions(season(1, 'Scheduled'))).toEqual(['start', 'cancel']);
    expect(component.actions(season(1, 'Cancelled'))).toEqual(['delete']);
  });

  it('does not offer deletion while a successor still references the cancelled season', () => {
    const cancelled = season(1, 'Cancelled');
    component.seasons = [cancelled, season(2, 'Scheduled', cancelled.id!)];

    expect(component.actions(cancelled)).toEqual([]);
  });
});
