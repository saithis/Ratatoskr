import { Component, OnInit, OnDestroy, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ApiService } from '../core/api.service';
import { PollingService } from '../core/polling.service';
import { BackendHealthDto } from '../models/api.types';

@Component({
  selector: 'rat-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    <h1>Service Dashboard</h1>

    @if (loading()) {
      <div style="display:flex;justify-content:center;padding:48px">
        <mat-spinner />
      </div>
    } @else if (error()) {
      <p class="rat-status-unhealthy">Failed to load dashboard: {{ error() }}</p>
    } @else if (backends().length === 0) {
      <div style="text-align:center;padding:48px;color:var(--rat-text-muted)">
        <mat-icon style="font-size:48px;width:48px;height:48px">hub</mat-icon>
        <p>No backends registered.</p>
      </div>
    } @else {
      <div style="display:grid;gap:16px;grid-template-columns:repeat(auto-fill,minmax(280px,1fr))">
        @for (b of backends(); track b.name) {
          <mat-card class="rat-card" style="cursor:pointer">
            <mat-card-header>
              <mat-icon mat-card-avatar [class]="b.healthy ? 'rat-status-healthy' : 'rat-status-unhealthy'">
                {{ b.healthy ? 'check_circle' : 'error' }}
              </mat-icon>
              <mat-card-title>{{ b.name }}</mat-card-title>
              <mat-card-subtitle>{{ b.isLocal ? 'Local' : 'Remote' }}</mat-card-subtitle>
            </mat-card-header>
            <mat-card-actions>
              <a [routerLink]="['/service', b.name, 'default']" mat-button color="primary">
                View Details
              </a>
            </mat-card-actions>
          </mat-card>
        }
      </div>
    }
  `,
})
export class DashboardComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly polling = inject(PollingService);

  readonly backends = signal<BackendHealthDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  private unsubscribePoll?: () => void;

  ngOnInit(): void {
    this.load();
    this.unsubscribePoll = this.polling.onTick(() => this.load());
  }

  ngOnDestroy(): void {
    this.unsubscribePoll?.();
  }

  private load(): void {
    this.loading.set(true);
    this.api.getDashboard().subscribe({
      next: dto => {
        this.backends.set(dto.backends);
        this.loading.set(false);
        this.error.set(null);
      },
      error: err => {
        this.error.set(err.message ?? 'Unknown error');
        this.loading.set(false);
      },
    });
  }
}
