using Hangfire;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Services.TestGen;

public interface ITestGenService
{
    Task<Result<Guid>> TriggerGenerationAsync(Guid tenantId, Guid? documentId, string scriptType, CancellationToken ct = default);
    Task<Result<GeneratedTestScript>> GetStatusAsync(Guid id, CancellationToken ct = default);
    Task<Result<Stream>> DownloadAsync(Guid id, CancellationToken ct = default);
}

public class TestGenService(
    IRepository<GeneratedTestScript> repository,
    IBlobStorage blobStorage,
    IBackgroundJobClient jobClient) : ITestGenService
{
    public async Task<Result<Guid>> TriggerGenerationAsync(
        Guid tenantId, Guid? documentId, string scriptType, CancellationToken ct = default)
    {
        var script = new GeneratedTestScript
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            ScriptType = scriptType,
            Status = ScriptStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        var result = await repository.AddAsync(script, ct);
        if (!result.Success)
            return Result<Guid>.Fail(result.Error!);

        jobClient.Enqueue<ITestGenJob>(job => job.RunAsync(script.Id, CancellationToken.None));

        return Result<Guid>.Ok(script.Id);
    }

    public async Task<Result<GeneratedTestScript>> GetStatusAsync(Guid id, CancellationToken ct = default)
    {
        var script = await repository.GetByIdAsync(id, ct);
        return script is null
            ? Result<GeneratedTestScript>.Fail("Not found.")
            : Result<GeneratedTestScript>.Ok(script);
    }

    public async Task<Result<Stream>> DownloadAsync(Guid id, CancellationToken ct = default)
    {
        var script = await repository.GetByIdAsync(id, ct);
        if (script is null)
            return Result<Stream>.Fail("Not found.");
        if (script.Status != ScriptStatus.Completed)
            return Result<Stream>.Fail($"Script is not ready (status: {script.Status}).");
        if (string.IsNullOrEmpty(script.BlobPath))
            return Result<Stream>.Fail("Blob path not recorded.");

        try
        {
            var stream = await blobStorage.OpenReadAsync(script.BlobPath, ct);
            return Result<Stream>.Ok(stream);
        }
        catch (FileNotFoundException)
        {
            return Result<Stream>.Fail("File not found in storage.");
        }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddTestGenServices(this IServiceCollection services)
    {
        services.AddScoped<ITestGenService, TestGenService>();
        services.AddScoped<ITestGenJob, TestGenJob>();
        return services;
    }
}
