namespace Rexo.Execution.Secrets;

using Rexo.Core.Abstractions;

public sealed class SecretProviderRegistry
{
    private readonly Dictionary<string, ISecretProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public static SecretProviderRegistry CreateDefault()
    {
        var registry = new SecretProviderRegistry();
        registry.Register(new ExecSecretProvider());
        registry.Register(new OnePasswordSecretProvider());
        return registry;
    }

    public void Register(ISecretProvider provider)
    {
        _providers[provider.Type] = provider;
    }

    public bool TryResolve(string type, out ISecretProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            provider = null;
            return false;
        }

        return _providers.TryGetValue(type, out provider);
    }
}
