import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { ApiService } from '../core/api.service';
import {
  ContextListItem,
  OutboxPoisonedListItem,
  InboxPoisonedListItem,
  BulkActionResult,
} from '../models/api.types';

@Component({
  selector: 'rat-service-detail',
  standalone: true,
  imports: [
    CommonModule,
    MatTabsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatChipsModule,
  ],
  template: `
    <h1>{{ backendName() }}</h1>

    @if (loadingContexts()) {
      <mat-spinner />
    } @else {
      <mat-tab-group>
        @for (ctx of contexts(); track ctx.name) {
          <mat-tab [label]="ctx.name">
            <div style="padding:16px">
              <mat-tab-group>

                <!-- Outbox Tab -->
                @if (ctx.hasOutbox) {
                  <mat-tab label="Outbox">
                    <div style="padding:16px">
                      <div style="display:flex;gap:8px;margin-bottom:12px;flex-wrap:wrap">
                        <button mat-stroked-button color="warn"
                          (click)="bulkRequeueOutbox(backendName(), ctx.name, { all: true })">
                          Requeue All
                        </button>
                        <button mat-stroked-button color="warn"
                          (click)="bulkDeleteOutbox(backendName(), ctx.name, { all: true })">
                          Delete All
                        </button>
                      </div>

                      @if (outboxItems(ctx.name)?.length === 0) {
                        <p style="color:var(--rat-text-muted);text-align:center;padding:32px">
                          No poisoned outbox messages.
                        </p>
                      } @else {
                        <table mat-table [dataSource]="outboxItems(ctx.name) ?? []" style="width:100%">
                          <ng-container matColumnDef="messageType">
                            <th mat-header-cell *matHeaderCellDef>Type</th>
                            <td mat-cell *matCellDef="let row">{{ row.messageType }}</td>
                          </ng-container>
                          <ng-container matColumnDef="createdAt">
                            <th mat-header-cell *matHeaderCellDef>Created</th>
                            <td mat-cell *matCellDef="let row">{{ row.createdAt | date:'short' }}</td>
                          </ng-container>
                          <ng-container matColumnDef="errorCount">
                            <th mat-header-cell *matHeaderCellDef>Errors</th>
                            <td mat-cell *matCellDef="let row">{{ row.errorCount }}</td>
                          </ng-container>
                          <ng-container matColumnDef="actions">
                            <th mat-header-cell *matHeaderCellDef></th>
                            <td mat-cell *matCellDef="let row">
                              <button mat-icon-button (click)="requeueOutbox(backendName(), ctx.name, row.id)" title="Requeue">
                                <mat-icon>replay</mat-icon>
                              </button>
                              <button mat-icon-button color="warn" (click)="deleteOutbox(backendName(), ctx.name, row.id)" title="Delete">
                                <mat-icon>delete</mat-icon>
                              </button>
                            </td>
                          </ng-container>
                          <tr mat-header-row *matHeaderRowDef="outboxColumns"></tr>
                          <tr mat-row *matRowDef="let row; columns: outboxColumns;"></tr>
                        </table>
                      }
                    </div>
                  </mat-tab>
                }

                <!-- Inbox Tab -->
                @if (ctx.hasInbox) {
                  <mat-tab label="Inbox">
                    <div style="padding:16px">
                      <div style="display:flex;gap:8px;margin-bottom:12px;flex-wrap:wrap">
                        <button mat-stroked-button color="warn"
                          (click)="bulkRequeueInbox(backendName(), ctx.name, { all: true })">
                          Requeue All
                        </button>
                        <button mat-stroked-button color="warn"
                          (click)="bulkDeleteInbox(backendName(), ctx.name, { all: true })">
                          Delete All
                        </button>
                      </div>

                      @if (inboxItems(ctx.name)?.length === 0) {
                        <p style="color:var(--rat-text-muted);text-align:center;padding:32px">
                          No poisoned inbox messages.
                        </p>
                      } @else {
                        <table mat-table [dataSource]="inboxItems(ctx.name) ?? []" style="width:100%">
                          <ng-container matColumnDef="messageType">
                            <th mat-header-cell *matHeaderCellDef>Type</th>
                            <td mat-cell *matCellDef="let row">{{ row.messageType }}</td>
                          </ng-container>
                          <ng-container matColumnDef="handlerName">
                            <th mat-header-cell *matHeaderCellDef>Handler</th>
                            <td mat-cell *matCellDef="let row">{{ row.handlerName }}</td>
                          </ng-container>
                          <ng-container matColumnDef="errorCount">
                            <th mat-header-cell *matHeaderCellDef>Errors</th>
                            <td mat-cell *matCellDef="let row">{{ row.errorCount }}</td>
                          </ng-container>
                          <ng-container matColumnDef="actions">
                            <th mat-header-cell *matHeaderCellDef></th>
                            <td mat-cell *matCellDef="let row">
                              <button mat-icon-button
                                (click)="requeueInbox(backendName(), ctx.name, row.messageId, row.handlerStatusId)"
                                title="Requeue">
                                <mat-icon>replay</mat-icon>
                              </button>
                              <button mat-icon-button color="warn"
                                (click)="deleteInbox(backendName(), ctx.name, row.handlerStatusId)"
                                title="Delete">
                                <mat-icon>delete</mat-icon>
                              </button>
                            </td>
                          </ng-container>
                          <tr mat-header-row *matHeaderRowDef="inboxColumns"></tr>
                          <tr mat-row *matRowDef="let row; columns: inboxColumns;"></tr>
                        </table>
                      }
                    </div>
                  </mat-tab>
                }
              </mat-tab-group>
            </div>
          </mat-tab>
        }
      </mat-tab-group>
    }

    @if (bulkErrors().length) {
      <div class="rat-card" style="margin-top:16px;border-color:var(--rat-danger)">
        <p style="color:var(--rat-danger);font-weight:600">Some operations failed:</p>
        <ul>
          @for (e of bulkErrors(); track e) {
            <li>{{ e }}</li>
          }
        </ul>
      </div>
    }
  `,
})
export class ServiceDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);

  readonly backendName = signal('');
  readonly contexts = signal<ContextListItem[]>([]);
  readonly loadingContexts = signal(true);
  readonly bulkErrors = signal<string[]>([]);

  private readonly outboxMap = signal<Record<string, OutboxPoisonedListItem[]>>({});
  private readonly inboxMap = signal<Record<string, InboxPoisonedListItem[]>>({});

  readonly outboxColumns = ['messageType', 'createdAt', 'errorCount', 'actions'];
  readonly inboxColumns = ['messageType', 'handlerName', 'errorCount', 'actions'];

  outboxItems(contextName: string): OutboxPoisonedListItem[] | undefined {
    return this.outboxMap()[contextName];
  }

  inboxItems(contextName: string): InboxPoisonedListItem[] | undefined {
    return this.inboxMap()[contextName];
  }

  ngOnInit(): void {
    const backend = this.route.snapshot.paramMap.get('backend') ?? '';
    this.backendName.set(backend);
    this.loadContexts(backend);
  }

  private loadContexts(backend: string): void {
    this.api.getContexts(backend).subscribe({
      next: res => {
        this.contexts.set(res.contexts);
        this.loadingContexts.set(false);
        res.contexts.forEach(ctx => {
          if (ctx.hasOutbox) this.loadOutbox(backend, ctx.name);
          if (ctx.hasInbox) this.loadInbox(backend, ctx.name);
        });
      },
      error: () => this.loadingContexts.set(false),
    });
  }

  private loadOutbox(backend: string, contextName: string): void {
    this.api.listPoisonedOutbox(backend, contextName).subscribe({
      next: res => this.outboxMap.update(m => ({ ...m, [contextName]: res.items })),
    });
  }

  private loadInbox(backend: string, contextName: string): void {
    this.api.listPoisonedInbox(backend, contextName).subscribe({
      next: res => this.inboxMap.update(m => ({ ...m, [contextName]: res.items })),
    });
  }

  requeueOutbox(backend: string, ctx: string, id: string): void {
    this.api.requeueOutbox(backend, ctx, id).subscribe(() => this.loadOutbox(backend, ctx));
  }

  deleteOutbox(backend: string, ctx: string, id: string): void {
    this.api.deleteOutbox(backend, ctx, id).subscribe(() => this.loadOutbox(backend, ctx));
  }

  bulkRequeueOutbox(backend: string, ctx: string, body: any): void {
    this.api.bulkRequeueOutbox(backend, ctx, body).subscribe({
      next: r => { this.handleBulkResult(r); this.loadOutbox(backend, ctx); },
    });
  }

  bulkDeleteOutbox(backend: string, ctx: string, body: any): void {
    this.api.bulkDeleteOutbox(backend, ctx, body).subscribe({
      next: r => { this.handleBulkResult(r); this.loadOutbox(backend, ctx); },
    });
  }

  requeueInbox(backend: string, ctx: string, messageId: string, handlerStatusId: string): void {
    this.api.requeueInboxHandler(backend, ctx, messageId, handlerStatusId)
      .subscribe(() => this.loadInbox(backend, ctx));
  }

  deleteInbox(backend: string, ctx: string, handlerStatusId: string): void {
    this.api.deleteInboxHandler(backend, ctx, handlerStatusId)
      .subscribe(() => this.loadInbox(backend, ctx));
  }

  bulkRequeueInbox(backend: string, ctx: string, body: any): void {
    this.api.bulkRequeueInbox(backend, ctx, body).subscribe({
      next: r => { this.handleBulkResult(r); this.loadInbox(backend, ctx); },
    });
  }

  bulkDeleteInbox(backend: string, ctx: string, body: any): void {
    this.api.bulkDeleteInbox(backend, ctx, body).subscribe({
      next: r => { this.handleBulkResult(r); this.loadInbox(backend, ctx); },
    });
  }

  private handleBulkResult(r: BulkActionResult): void {
    this.bulkErrors.set(r.failed ?? []);
  }
}
