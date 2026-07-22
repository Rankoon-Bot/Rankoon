import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-level-progress',
  standalone: true,
  imports: [TranslocoPipe],
  template: `
    <svg class="progress" viewBox="0 0 44 44" role="img" [attr.aria-label]="'leaderboard.levelProgressAria' | transloco: { level: level(), progress: progressPercent() }">
      <circle class="track" cx="22" cy="22" r="18" stroke-width="4" />
      <circle class="value" cx="22" cy="22" r="18" stroke-width="4" [attr.stroke-dasharray]="circumference" [attr.stroke-dashoffset]="dashOffset()" />
      <text x="22" y="22" text-anchor="middle" dominant-baseline="central" font-size="12">{{ level() }}</text>
    </svg>
  `,
  styleUrl: './level-progress.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LevelProgressComponent {
  readonly level = input.required<number>();
  readonly totalXp = input.required<string | number>();
  readonly circumference = 2 * Math.PI * 18;
  readonly progress = computed(() => {
    const currentLevelXp = this.requiredXpForLevel(this.level());
    const nextLevelXp = this.requiredXpForLevel(this.level() + 1);
    const totalXp = Number(this.totalXp());
    return Math.max(0, Math.min(1, (totalXp - currentLevelXp) / (nextLevelXp - currentLevelXp)));
  });
  readonly dashOffset = computed(() => this.circumference * (1 - this.progress()));
  readonly progressPercent = computed(() => Math.round(this.progress() * 100));

  private requiredXpForLevel(level: number): number {
    if (level <= 0) return 0;
    if (level === 1) return 100;
    return Math.floor((5 / 6) * level * (2 * level * level + 27 * level + 91));
  }
}
