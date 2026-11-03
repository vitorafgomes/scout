import { inject } from '@angular/core';
import { Router, type Routes } from '@angular/router';

import { MenuPreferences } from './core/menu-preferences';

import { requireClientIdGuard, requireBotTokenGuard } from './core/wizard.guards';

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
    canActivate: [requireBotTokenGuard],
    loadComponent: () =>
      import('./wizard/worker-launch/worker-launch.component').then((m) => m.WorkerLaunchComponent),
  },
  {
    path: 'ready',
    loadComponent: () => import('./wizard/ready/ready.component').then((m) => m.ReadyComponent),
  },
  { path: 'status', redirectTo: 'logs' },
  {
    path: 'sources',
    loadComponent: () => import('./sources/sources.component').then((m) => m.SourcesComponent),
  },
  {
    path: 'sources/local-models',
    loadComponent: () =>
      import('./sources/local-models.component').then((m) => m.LocalModelsComponent),
  },
  {
    path: 'features',
    loadComponent: () => import('./features/features.component').then((m) => m.FeaturesComponent),
  },
  {
    path: 'production',
    loadComponent: () =>
      import('./production/production.component').then((m) => m.ProductionComponent),
  },
  {
    path: 'review',
    loadComponent: () => import('./review/review.component').then((m) => m.ReviewComponent),
  },
  {
    path: 'llm',
    loadComponent: () => import('./models/models.component').then((m) => m.ModelsComponent),
  },
  {
    path: 'logs',
    canActivate: [() => inject(MenuPreferences).showLogs() || inject(Router).parseUrl('/settings')],
    loadComponent: () => import('./wizard/log-viewer/logs.component').then((m) => m.LogsComponent),
  },
  {
    path: 'slack',
    loadComponent: () =>
      import('./wizard/slack/slack-setup.component').then((m) => m.SlackSetupComponent),
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./wizard/settings/settings.component').then((m) => m.SettingsComponent),
  },
  {
    path: 'chat',
    loadComponent: () => import('./chat/chat.component').then((m) => m.ChatComponent),
  },
  {
    path: 'quick-ask',
    loadComponent: () => import('./quick-ask/quick-ask.component').then((m) => m.QuickAskComponent),
  },
  {
    path: 'floating-button',
    loadComponent: () =>
      import('./floating-button/floating-button.component').then((m) => m.FloatingButtonComponent),
  },
  { path: '**', redirectTo: '' },
];
