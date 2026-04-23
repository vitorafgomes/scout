import type { Routes } from '@angular/router';

export const appRoutes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./wizard/welcome/welcome.component').then((m) => m.WelcomeComponent),
  },
  { path: '**', redirectTo: '' },
];
