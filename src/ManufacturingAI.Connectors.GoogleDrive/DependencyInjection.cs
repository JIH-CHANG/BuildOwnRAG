using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Connectors.GoogleDrive;

public static class DependencyInjection
{
    public static IServiceCollection AddGoogleDriveConnector(this IServiceCollection services)
    {
        services.AddScoped<IKnowledgeConnector, GoogleDriveConnector>();
        return services;
    }
}
