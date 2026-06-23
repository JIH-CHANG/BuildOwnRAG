using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Connectors.SharePoint;

public static class DependencyInjection
{
    public static IServiceCollection AddSharePointConnector(this IServiceCollection services)
    {
        services.AddScoped<IKnowledgeConnector, SharePointConnector>();
        return services;
    }
}
