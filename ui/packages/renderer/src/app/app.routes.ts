import type { Routes } from '@angular/router';

import { requireClientIdGuard, requireGuildGuard } from './core/wizard.guards';

export const appRoutes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./wizard/welcome/welcome.component').then((m) => m.WelcomeComponent),
  },
  {
    path: 'provision',
    loadComponent: () =>
      import('./wizard/provision/provision.component').then((m) => m.ProvisionComponent),
  },
  {
    path: 'token',
    loadComponent: () =>
      import('./wizard/token-paste/token-paste.component').then((m) => m.TokenPasteComponent),
  },
  {
    path: 'oauth',
    canActivate: [requireClientIdGuard],
    loadComponent: () =>
      import('./wizard/oauth-install/oauth-install.component').then((m) => m.OAuthInstallComponent),
  },
  {
    path: 'launch',
    canActivate: [requireGuildGuard],
    loadComponent: () =>
      import('./wizard/worker-launch/worker-launch.component').then((m) => m.WorkerLaunchComponent),
  },
  {
    path: 'ready',
    loadComponent: () => import('./wizard/ready/ready.component').then((m) => m.ReadyComponent),
  },
  { path: '**', redirectTo: '' },
];
