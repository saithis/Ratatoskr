// TypeScript interfaces matching the Ratatoskr management API DTOs.

export interface BackendDto {
  name: string;
  isLocal: boolean;
}

export interface BackendHealthDto {
  name: string;
  isLocal: boolean;
  healthy: boolean;
  error: string | null;
}

export interface DashboardDto {
  backends: BackendHealthDto[];
}

export interface ContextListItem {
  name: string;
  hasOutbox: boolean;
  hasInbox: boolean;
}

export interface ContextListResponse {
  contexts: ContextListItem[];
}

export interface ContextHealthResponse {
  contextName: string;
  outboxPoisonedCount: number;
  inboxPoisonedCount: number;
  lastOutboxProcessorRun: string | null;
  lastInboxProcessorRun: string | null;
}

export interface OutboxPoisonedListItem {
  id: string;
  messageType: string;
  createdAt: string;
  errorCount: number;
  requeuedException: number;
  lastError: string | null;
  dbContext: string;
}

export interface PaginatedList<T> {
  items: T[];
  totalCount: number;
  nextCursor: string | null;
}

export interface InboxPoisonedListItem {
  handlerStatusId: string;
  messageId: string;
  handlerName: string;
  messageType: string;
  createdAt: string;
  errorCount: number;
  requeuedCount: number;
  lastError: string | null;
  dbContext: string;
}

export interface BulkRequest {
  ids: string[];
}

export interface BulkActionResult {
  succeeded: number;
  failed: string[];
}
