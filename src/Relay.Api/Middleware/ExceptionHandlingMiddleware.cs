using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Relay.Application.Exceptions;

namespace Relay.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate _next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        this._next = _next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, problem) = exception switch
        {
            ValidationException valEx => (
                StatusCodes.Status400BadRequest,
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Failed",
                    Detail = valEx.Message,
                    Instance = context.Request.Path,
                    Extensions = { ["errors"] = valEx.Errors }
                }),

            IdempotencyConflictException conflictEx => (
                StatusCodes.Status409Conflict,
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Idempotency Conflict",
                    Detail = conflictEx.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["idempotencyKey"] = conflictEx.IdempotencyKey,
                        ["existingEventId"] = conflictEx.ExistingEventId
                    }
                }),

            PayloadTooLargeException payloadEx => (
                StatusCodes.Status413PayloadTooLarge,
                new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = "Payload Too Large",
                    Detail = payloadEx.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["actualBytes"] = payloadEx.ActualBytes,
                        ["maxAllowedBytes"] = payloadEx.MaxAllowedBytes
                    }
                }),

            ResourceNotFoundException notFoundEx => (
                StatusCodes.Status404NotFound,
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Resource Not Found",
                    Detail = notFoundEx.Message,
                    Instance = context.Request.Path
                }),

            UnauthorizedTenantException unauthorizedEx => (
                StatusCodes.Status401Unauthorized,
                new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthorized",
                    Detail = unauthorizedEx.Message,
                    Instance = context.Request.Path
                }),

            JsonException jsonEx => (
                StatusCodes.Status400BadRequest,
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Malformed JSON Request",
                    Detail = "The request payload contains malformed JSON.",
                    Instance = context.Request.Path
                }),

            _ => (
                StatusCodes.Status500InternalServerError,
                new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal Server Error",
                    Detail = "An unexpected error occurred while processing the request.",
                    Instance = context.Request.Path
                })
        };

        if (statusCode >= 500)
        {
            _logger.LogError(exception, "Unhandled exception occurred while processing {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }
        else
        {
            _logger.LogWarning("Request {Method} {Path} failed with status {StatusCode}: {Message}",
                context.Request.Method, context.Request.Path, statusCode, exception.Message);
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json");
    }
}
