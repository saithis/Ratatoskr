import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';
import { ThemeService } from './core/theme.service';
import { PollingService } from './core/polling.service';

@Component({
  selector: 'rat-root',
  standalone: true,
  imports: [RouterOutlet, MatToolbarModule, MatIconModule, MatButtonModule, RouterLink],
  template: `
    <mat-toolbar color="primary">
      <a [routerLink]="['/']" style="color:inherit;text-decoration:none;display:flex;align-items:center;gap:8px">
        <mat-icon>hub</mat-icon>
        <span>Ratatoskr</span>
      </a>
      <span style="flex:1"></span>
      <span style="font-size:13px;opacity:.7;margin-right:8px">
        Refresh in {{ polling.countdown() }}s
      </span>
      <button mat-icon-button (click)="theme.toggle()" [title]="theme.isDark() ? 'Switch to light' : 'Switch to dark'">
        <mat-icon>{{ theme.isDark() ? 'light_mode' : 'dark_mode' }}</mat-icon>
      </button>
    </mat-toolbar>

    <main class="rat-page">
      <router-outlet />
    </main>
  `,
})
export class AppComponent {
  readonly theme = inject(ThemeService);
  readonly polling = inject(PollingService);
}
