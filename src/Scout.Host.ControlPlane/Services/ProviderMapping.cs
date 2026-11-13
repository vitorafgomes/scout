using Scout.ControlPlane.Providers;
using Contracts = Scout.ControlPlane.Contracts;
using DomainModel = Scout.ControlPlane.Providers.ProviderModel;
using DomainStatus = Scout.ControlPlane.Providers.ProviderStatus;

namespace Scout.Host.ControlPlane.Services;

/// <summary>Maps provider wire ids and snapshots to/from the domain.</summary>
internal static class ProviderMapping
{
    public static string ToId(Contracts.ProviderId id) => id switch
    {
        Contracts.ProviderId.Ollama => ProvidersService.OllamaId,
        Contracts.ProviderId.LmStudio => ProvidersService.LmStudioId,
        _ => throw new ProviderUnknownException(id.ToString()),
    };

    public static Contracts.ProviderId ToWireId(string id) => id switch
    {
        ProvidersService.OllamaId => Contracts.ProviderId.Ollama,
        ProvidersService.LmStudioId => Contracts.ProviderId.LmStudio,
        _ => Contracts.ProviderId.Unspecified,
    };

    public static Contracts.ProviderStatus ToWire(DomainStatus status)
    {
        var wire = new Contracts.ProviderStatus
        {
            Id = ToWireId(status.Id),
            Installed = status.Installed,
            Running = status.Running,
            ManagedByWorker = status.ManagedByWorker,
            Autostart = status.Autostart,
            SupportsLifecycle = status.SupportsLifecycle,
        };

        if (status.Endpoint is not null)
        {
            wire.Endpoint = status.Endpoint;
        }
        if (status.Version is not null)
        {
            wire.Version = status.Version;
        }
        if (status.LastError is not null)
        {
            wire.LastError = new Contracts.ProviderError
            {
                Code = status.LastError.Code,
                Message = status.LastError.Message,
            };
        }
        foreach (var model in status.Models)
        {
            wire.Models.Add(ToWire(model));
        }

        return wire;
    }

    public static Contracts.ProviderModel ToWire(DomainModel model) => new()
    {
        Name = model.Name,
        SizeBytes = model.SizeBytes,
        ModifiedAt = model.ModifiedAt,
    };
}
