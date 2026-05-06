import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./dashboard/dashboard.component').then(m => m.DashboardComponent),
  },
  {
    path: 'service/:backend/:name',
    loadComponent: () =>
      import('./service-detail/service-detail.component').then(m => m.ServiceDetailComponent),
  },
  { path: '**', redirectTo: '' },
];
