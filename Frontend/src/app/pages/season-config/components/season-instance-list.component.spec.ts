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

  it('offers direct deletion and a break for planned seasons', () => {
    expect(component.actions(season(1, 'Scheduled'))).toEqual(['pause', 'delete']);
    expect(component.actions(season(1, 'Cancelled'))).toEqual(['resume', 'delete']);
  });

  it('does not offer deletion while a successor still references the cancelled season', () => {
    const cancelled = season(1, 'Cancelled');
    component.seasons = [cancelled, season(2, 'Scheduled', cancelled.id!)];

    expect(component.actions(cancelled)).toEqual([]);
  });

  it('offers starting only inside the period with seasons enabled and no other live season', () => {
    const current = { ...season(1, 'Scheduled'), startsAtUtc: new Date(Date.now() - 60000).toISOString(), endsAtUtc: new Date(Date.now() + 60000).toISOString() };
    component.seasons = [current];
    expect(component.actions(current)).toEqual(['start', 'pause', 'delete']);
    component.enabled = false;
    expect(component.actions(current)).toEqual(['pause', 'delete']);
    component.enabled = true;
    component.seasons.push(season(2, 'Closing'));
    expect(component.actions(current)).toEqual(['pause', 'delete']);
  });
});
