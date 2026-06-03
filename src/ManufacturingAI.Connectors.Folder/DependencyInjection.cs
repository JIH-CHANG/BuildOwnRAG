using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Connectors.Folder;

public static class DependencyInjection
{
    public static IServiceCollection AddFolderConnector(this IServiceCollection services)
    {
        services.AddScoped<IKnowledgeConnector, FolderConnector>();
        return services;
    }
}
