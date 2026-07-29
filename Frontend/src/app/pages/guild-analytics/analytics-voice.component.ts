import { Component } from '@angular/core';
import { AnalyticsPageComponent } from './analytics-page.component';

@Component({ selector: 'app-analytics-voice', standalone: true, imports: [AnalyticsPageComponent], template: '<app-analytics-page page="voice" />' })
export class AnalyticsVoiceComponent {}
