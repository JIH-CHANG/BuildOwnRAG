using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Connectors.Confluence;

public static class DependencyInjection
{
    public static IServiceCollection AddConfluenceConnector(this IServiceCollection services)
    {
        services.AddScoped<IKnowledgeConnector, ConfluenceConnector>();
        return services;
    }
}
