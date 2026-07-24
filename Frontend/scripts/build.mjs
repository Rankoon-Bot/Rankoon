import { spawnSync } from 'node:child_process';

const buildVersion = process.env.APP_VERSION ?? '0.0.0-local';

if (!/^[0-9A-Za-z.+-]+$/.test(buildVersion)) {
  throw new Error('APP_VERSION may contain only letters, numbers, dots, plus signs, and hyphens.');
}

const result = spawnSync(process.execPath, [
  './node_modules/@angular/cli/bin/ng.js',
  'build',
  `--define=BUILD_VERSION="${buildVersion}"`,
], { stdio: 'inherit' });

process.exit(result.status ?? 1);
