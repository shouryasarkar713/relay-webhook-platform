# ADR-0005: React and TypeScript Developer Console

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay requires an intuitive, modern developer console allowing developers and operators to test API-key authentication, submit test events, verify payload hashes and idempotency behavior, and inspect event ingestion history.

The candidate's target internship role emphasizes proficiency in C#/.NET for backend services paired with React and TypeScript for modern cloud SaaS user interfaces.

## 2. Problem

What frontend stack and client-side architecture best serves the developer console requirements while ensuring security best practices (no leaked credentials) and a clean, maintainable, type-safe codebase?

## 3. Decision

We choose **React 19**, **TypeScript**, and **Vite** located in `frontend/relay-dashboard`.

Key engineering decisions:
1. **TypeScript First**: Strict type definitions mirror backend DTO contracts (`EventDto`, `IngestEventRequest`, `IngestEventResponse`, `ProblemDetails`).
2. **In-Memory Credential Isolation**:
   - The developer console accepts an API key to execute requests against the Relay backend.
   - The API key is stored exclusively in ephemeral React component/hook state (`useState`).
   - The key is **never** written to `localStorage`, `sessionStorage`, cookies, or browser caches.
   - The key is excluded from console logs, error telemetry, or URL query parameters.
   - Reloading the page immediately purges the credential from memory.
3. **Centralized Typed HTTP Client**:
   A dedicated `RelayApiClient` encapsulates `fetch` logic, default headers (`Content-Type`, `X-Api-Key`, `Idempotency-Key`), error parsing (`ProblemDetails`), and abort signals. Components never execute raw, scattered `fetch` calls.
4. **Restrained, Professional SaaS UI**:
   A clean dark/light developer aesthetic with a collapsible sidebar, status indicators, JSON syntax display, clear idempotency status feedback, and explicit "Coming Soon" badges for future milestone pages (Endpoints, Retries, Circuit Breakers).

## 4. Alternatives Considered

1. **Blazor WebAssembly / Blazor Server**:
   - *Pros*: Single C# language across frontend and backend.
   - *Cons*: Explicitly rejected because the internship role requires demonstrated competence in React and TypeScript. Furthermore, Blazor WASM has larger bundle download sizes and slower initial startup.
2. **Plain HTML / Vanilla JavaScript**:
   - *Pros*: Zero build tooling.
   - *Cons*: Lacks type safety, component reusability, state management, and modern developer tooling. Unsuitable for demonstrating professional frontend engineering capabilities.

## 5. Consequences & Trade-offs

### Positive
- Strict alignment with modern web development industry standards.
- Compile-time type verification catches API contract mismatches early.
- Zero credential leakage surface on client storage.
- Fast local development cycle using Vite HMR (Hot Module Replacement).

### Negative / Trade-offs
- Requires separate Node.js / npm toolchain during development and CI.
- Frontend and backend schemas must be kept synchronized across language boundaries (addressed via well-defined DTO interfaces).
