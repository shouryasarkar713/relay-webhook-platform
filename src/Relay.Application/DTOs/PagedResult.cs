namespace Relay.Application.DTOs;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Count,
    int Take);
