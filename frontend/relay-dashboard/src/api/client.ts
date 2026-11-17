import type {
  IngestEventRequest,
  IngestEventResponse,
  EventDto,
  PagedResult,
  ProblemDetails,
  HealthStatus
} from '../types/api';

export class ApiError extends Error {
  public readonly status: number;
  public readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail || problem.title || `API error ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

export class RelayApiClient {
  private readonly baseUrl: string;

  constructor(baseUrl?: string) {
    this.baseUrl = (baseUrl || import.meta.env.VITE_API_URL || 'http://localhost:5000').replace(/\/+$/, '');
  }

  public async checkLive(): Promise<HealthStatus> {
    const response = await fetch(`${this.baseUrl}/health/live`);
    if (!response.ok) {
      throw new Error(`Liveness check failed with status ${response.status}`);
    }
    return response.json();
  }

  public async checkReady(): Promise<HealthStatus> {
    try {
      const response = await fetch(`${this.baseUrl}/health/ready`);
      return response.json();
    } catch (err) {
      return {
        status: 'Unreachable',
        database: 'Offline',
        timestampUtc: new Date().toISOString(),
        error: (err as Error).message
      };
    }
  }

  public async ingestEvent(
    apiKey: string,
    idempotencyKey: string,
    request: IngestEventRequest
  ): Promise<{ response: IngestEventResponse; status: number; location: string | null; durationMs: number }> {
    const startTime = performance.now();

    const response = await fetch(`${this.baseUrl}/v1/events`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Api-Key': apiKey,
        'Idempotency-Key': idempotencyKey,
        'Accept': 'application/json'
      },
      body: JSON.stringify(request)
    });

    const durationMs = Math.round(performance.now() - startTime);
    const location = response.headers.get('Location');

    if (!response.ok) {
      let problem: ProblemDetails;
      try {
        problem = await response.json();
      } catch {
        problem = {
          status: response.status,
          title: response.statusText || 'Error processing request',
          detail: await response.text()
        };
      }
      throw new ApiError(response.status, problem);
    }

    const data: IngestEventResponse = await response.json();
    return { response: data, status: response.status, location, durationMs };
  }

  public async getEvent(apiKey: string, eventId: string): Promise<EventDto> {
    const response = await fetch(`${this.baseUrl}/v1/events/${eventId}`, {
      method: 'GET',
      headers: {
        'X-Api-Key': apiKey,
        'Accept': 'application/json'
      }
    });

    if (!response.ok) {
      let problem: ProblemDetails;
      try {
        problem = await response.json();
      } catch {
        problem = {
          status: response.status,
          title: 'Not Found',
          detail: `Event ${eventId} not found`
        };
      }
      throw new ApiError(response.status, problem);
    }

    return response.json();
  }

  public async listEvents(apiKey: string, take: number = 20): Promise<PagedResult<EventDto>> {
    const response = await fetch(`${this.baseUrl}/v1/events?take=${take}`, {
      method: 'GET',
      headers: {
        'X-Api-Key': apiKey,
        'Accept': 'application/json'
      }
    });

    if (!response.ok) {
      let problem: ProblemDetails;
      try {
        problem = await response.json();
      } catch {
        problem = {
          status: response.status,
          title: 'Error loading events',
          detail: response.statusText
        };
      }
      throw new ApiError(response.status, problem);
    }

    return response.json();
  }
}

export const apiClient = new RelayApiClient();
