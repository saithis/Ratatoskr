import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  BackendDto,
  DashboardDto,
  ContextListResponse,
  ContextHealthResponse,
  OutboxPoisonedListItem,
  InboxPoisonedListItem,
  PaginatedList,
  BulkRequest,
  BulkActionResult,
} from '../models/api.types';

const BASE = '/ratatoskr/api/v1';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ---- Backends ----

  getBackends(): Observable<BackendDto[]> {
    return this.http.get<BackendDto[]>(`${BASE}/backends`);
  }

  getDashboard(): Observable<DashboardDto> {
    return this.http.get<DashboardDto>(`${BASE}/dashboard`);
  }

  // ---- Per-backend helpers ----

  private backendBase(backend: string): string {
    return `${BASE}/backends/${backend}`;
  }

  // ---- EF Core contexts ----

  getContexts(backend: string): Observable<ContextListResponse> {
    return this.http.get<ContextListResponse>(`${this.backendBase(backend)}/efcore/contexts`);
  }

  getContextHealth(backend: string, context: string): Observable<ContextHealthResponse> {
    return this.http.get<ContextHealthResponse>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/health`,
    );
  }

  // ---- Outbox ----

  listPoisonedOutbox(
    backend: string,
    context: string,
    options: { pageSize?: number; cursor?: string; search?: string } = {},
  ): Observable<PaginatedList<OutboxPoisonedListItem>> {
    let params = new HttpParams();
    if (options.pageSize) params = params.set('pageSize', options.pageSize);
    if (options.cursor) params = params.set('cursor', options.cursor);
    if (options.search) params = params.set('search', options.search);
    return this.http.get<PaginatedList<OutboxPoisonedListItem>>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/outbox/poisoned`,
      { params },
    );
  }

  requeueOutbox(backend: string, context: string, id: string): Observable<void> {
    return this.http.post<void>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/outbox/${id}/requeue`,
      null,
    );
  }

  deleteOutbox(backend: string, context: string, id: string): Observable<void> {
    return this.http.delete<void>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/outbox/${id}`,
    );
  }

  bulkRequeueOutbox(backend: string, context: string, body: BulkRequest | { all: true }): Observable<BulkActionResult> {
    const suffix = 'all' in body ? '/all' : '';
    return this.http.post<BulkActionResult>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/outbox/poisoned/requeue${suffix}`,
      body,
    );
  }

  bulkDeleteOutbox(backend: string, context: string, body: BulkRequest | { all: true }): Observable<BulkActionResult> {
    const suffix = 'all' in body ? '/all' : '';
    return this.http.post<BulkActionResult>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/outbox/poisoned/delete${suffix}`,
      body,
    );
  }

  // ---- Inbox ----

  listPoisonedInbox(
    backend: string,
    context: string,
    options: { pageSize?: number; cursor?: string; search?: string } = {},
  ): Observable<PaginatedList<InboxPoisonedListItem>> {
    let params = new HttpParams();
    if (options.pageSize) params = params.set('pageSize', options.pageSize);
    if (options.cursor) params = params.set('cursor', options.cursor);
    if (options.search) params = params.set('search', options.search);
    return this.http.get<PaginatedList<InboxPoisonedListItem>>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/inbox/poisoned`,
      { params },
    );
  }

  requeueInboxHandler(
    backend: string,
    context: string,
    messageId: string,
    handlerStatusId: string,
  ): Observable<void> {
    return this.http.post<void>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/inbox/${messageId}/handlers/${handlerStatusId}/requeue`,
      null,
    );
  }

  deleteInboxHandler(backend: string, context: string, handlerStatusId: string): Observable<void> {
    return this.http.delete<void>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/inbox/${handlerStatusId}`,
    );
  }

  bulkRequeueInbox(backend: string, context: string, body: BulkRequest | { all: true }): Observable<BulkActionResult> {
    const suffix = 'all' in body ? '/all' : '';
    return this.http.post<BulkActionResult>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/inbox/poisoned/requeue${suffix}`,
      body,
    );
  }

  bulkDeleteInbox(backend: string, context: string, body: BulkRequest | { all: true }): Observable<BulkActionResult> {
    const suffix = 'all' in body ? '/all' : '';
    return this.http.post<BulkActionResult>(
      `${this.backendBase(backend)}/efcore/contexts/${context}/inbox/poisoned/delete${suffix}`,
      body,
    );
  }
}
