import { Component } from '@angular/core';
import { AnalyticsPageComponent } from './analytics-page.component';

@Component({ selector: 'app-analytics-overview', standalone: true, imports: [AnalyticsPageComponent], template: '<app-analytics-page page="overview" />' })
export class AnalyticsOverviewComponent {}
