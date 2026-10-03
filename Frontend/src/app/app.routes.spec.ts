import { botMaintainerGuard, botOperatorGuard, guildGuard, moduleGuard } from './guards/auth.guard';
import { routes } from './app.routes';

describe('application routes', () => {
  const children = routes.find(route => route.path === '')!.children!;
  it('guards the analytics shell and inherits its authorization for child pages', () => {
    const analytics = children.find(route => route.path === 'analytics')!;
    expect(analytics.canActivate).toEqual([guildGuard, moduleGuard]); expect(analytics.data?.['module']).toBe('analytics');
    for (const route of analytics.children!.filter(route => route.loadComponent)) expect(route.canActivate).toBeUndefined();
  });
  it('guards the Bot Operations shell and operator pages', () => {
    const operations = children.find(route => route.path === 'bot-management')!; expect(operations.canActivate).toEqual([botOperatorGuard]);
    for (const route of operations.children!.filter(route => route.loadComponent && route.path !== 'maintainer')) expect(route.canActivate).toEqual([botOperatorGuard]);
  });
  it('requires maintainer access for the maintainer page within the guarded operations shell', () => {
    const operations = children.find(route => route.path === 'bot-management')!;
    expect(operations.canActivate).toEqual([botOperatorGuard]);
    expect(operations.children!.find(route => route.path === 'maintainer')?.canActivate).toEqual([botMaintainerGuard]);
  });
  it('redirects legacy logs and never loads guild error logs', () => {
    expect(children.find(route => route.path === 'logs/errors')?.redirectTo).toBe('/analytics/audit');
    expect(JSON.stringify(children)).not.toContain('ErrorLogsComponent');
  });
});
