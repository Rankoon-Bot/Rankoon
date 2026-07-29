export type AnalyticsUnit = 'xp' | 'count' | 'duration' | 'members' | 'percent';

export interface AnalyticsChartSeries {
  key: string;
  label: string;
  className?: string;
}

export interface AnalyticsChartPoint {
  timestamp: string;
  end?: string;
  isIncomplete?: boolean;
  values: Readonly<Record<string, number>>;
}
