namespace Relay.Application.Abstractions;

public interface IEndpointSecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
