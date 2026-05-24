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
  {
    path: 'status',
    loadComponent: () => import('./wizard/ready/ready.component').then((m) => m.ReadyComponent),
  },
  {
    path: 'sources',
    loadComponent: () =>
      import('./sources/sources.component').then((m) => m.SourcesComponent),
  },
  {
    path: 'sources/local-models',
    loadComponent: () =>
      import('./sources/local-models.component').then((m) => m.LocalModelsComponent),
  },
  {
    path: 'features',
    loadComponent: () =>
      import('./features/features.component').then((m) => m.FeaturesComponent),
  },
  {
    path: 'llm',
    loadComponent: () => import('./models/models.component').then((m) => m.ModelsComponent),
  },
  {
    path: 'logs',
    loadComponent: () => import('./wizard/log-viewer/logs.component').then((m) => m.LogsComponent),
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./wizard/settings/settings.component').then((m) => m.SettingsComponent),
  },
  { path: '**', redirectTo: '' },
];
