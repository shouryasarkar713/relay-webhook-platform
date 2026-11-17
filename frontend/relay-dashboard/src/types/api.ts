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

export interface WebhookEndpointSubscriptionDto {
  eventType: string;
}

export interface WebhookEndpointResponse {
  id: string;
  name: string;
  url: string;
  isActive: boolean;
  circuitState: 'Closed' | 'Open' | 'HalfOpen';
  consecutiveFailureCount: number;
  circuitOpenedAtUtc?: string | null;
  createdAtUtc: string;
  disabledAtUtc?: string | null;
  eventTypes: string[];
  signingSecret?: string | null;
}

export interface CreateWebhookEndpointRequest {
  name: string;
  url: string;
  eventTypes: string[];
}

export interface UpdateWebhookEndpointRequest {
  name?: string;
  url?: string;
  eventTypes?: string[];
  isActive?: boolean;
}

export interface WebhookDeliveryAttemptDto {
  id: string;
  attemptNumber: number;
  startedAtUtc: string;
  completedAtUtc: string;
  durationMs: number;
  httpStatusCode?: number | null;
  outcome: string;
  errorCategory?: string | null;
  responseExcerpt?: string | null;
}

export interface WebhookDeliveryDto {
  id: string;
  eventId: string;
  endpointId: string;
  endpointUrl: string;
  endpointName: string;
  eventType: string;
  status: string;
  attemptCount: number;
  claimCount: number;
  replayCount: number;
  nextAttemptAtUtc: string;
  deadLetteredAtUtc?: string | null;
  lastHttpStatusCode?: number | null;
  lastErrorCategory?: string | null;
  lastError?: string | null;
  createdAtUtc: string;
  deliveredAtUtc?: string | null;
  attempts: WebhookDeliveryAttemptDto[];
}

export interface ReplayDeliveryResponse {
  deliveryId: string;
  status: string;
  replayedAtUtc: string;
  replayCount: number;
  message: string;
}
