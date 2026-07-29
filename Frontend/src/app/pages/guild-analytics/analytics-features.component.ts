import { Component } from '@angular/core';
import { AnalyticsPageComponent } from './analytics-page.component';

@Component({ selector: 'app-analytics-features', standalone: true, imports: [AnalyticsPageComponent], template: '<app-analytics-page page="features" />' })
export class AnalyticsFeaturesComponent {}
