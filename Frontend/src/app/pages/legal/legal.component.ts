import { Component, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-legal',
  standalone: true,
  imports: [RouterLink, TranslocoPipe],
  template: `
    <article class="legal-page">
      <a class="back-link" routerLink="/">&larr; {{ "legal.back" | transloco }}</a>
      <header>
        <p class="overline">{{ "legal.overline" | transloco }}</p>
        <h1>{{ ("legal." + documentKey + ".title") | transloco }}</h1>
        <p class="intro">{{ ("legal." + documentKey + ".intro") | transloco }}</p>
      </header>
      @for (section of sections; track section) {
        <section>
          <h2>{{ section }}. {{ ("legal." + documentKey + ".section" + section + ".title") | transloco }}</h2>
          @if (!isPrivacy && section === 6) {
            <p>{{ "legal.terms.section6.beforeLink" | transloco }}<a routerLink="/privacy">{{ "legal.privacy.title" | transloco }}</a>{{ "legal.terms.section6.afterLink" | transloco }}</p>
          } @else {
            <p>{{ ("legal." + documentKey + ".section" + section + ".body") | transloco }}</p>
          }
          @if (isPrivacy && section === 2) {
            <p>{{ "legal.privacy.section2.activity" | transloco }}</p>
          }
        </section>
      }
    </article>
  `,
  styleUrl: './legal.component.scss',
})
export class LegalComponent {
  readonly sections = [1, 2, 3, 4, 5, 6, 7];
  readonly isPrivacy = inject(ActivatedRoute).snapshot.data['page'] === 'privacy';
  readonly documentKey = this.isPrivacy ? 'privacy' : 'terms';
}
