namespace Relay.Domain.Entities;

public sealed class Tenant
{
    private readonly List<ApiKey> _apiKeys = [];
    private readonly List<Event> _events = [];

    private Tenant() { }

    public Tenant(Guid id, string name, string slug, DateTimeOffset createdAtUtc, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name cannot be empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("Tenant slug cannot be empty.", nameof(slug));
        }

        Id = id;
        Name = name.Trim();
        Slug = slug.Trim().ToLowerInvariant();
        CreatedAtUtc = createdAtUtc;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyCollection<ApiKey> ApiKeys => _apiKeys.AsReadOnly();
    public IReadOnlyCollection<Event> Events => _events.AsReadOnly();

    public void Deactivate()
    {
        IsActive = false;
    }

    public void Activate()
    {
        IsActive = true;
    }
}
