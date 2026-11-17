export interface IngestEventRequest {
  eventType: string;
  payload: Record<string, unknown> | unknown[];
}

export interface IngestEventResponse {
  eventId: string;
  status: string;
  createdAtUtc: string;
}

export interface EventDto {
  id: string;
  tenantId: string;
  eventType: string;
  payloadJson: string;
  payloadHash: string;
  idempotencyKey: string;
  createdAtUtc: string;
  status: string;
}

export interface PagedResult<T> {
  items: T[];
  count: number;
  take: number;
}

export interface ProblemDetails {
  status: number;
  title: string;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
  idempotencyKey?: string;
  existingEventId?: string;
  [key: string]: unknown;
}

export interface HealthStatus {
  status: string;
  database?: string;
  timestampUtc: string;
  error?: string;
}
