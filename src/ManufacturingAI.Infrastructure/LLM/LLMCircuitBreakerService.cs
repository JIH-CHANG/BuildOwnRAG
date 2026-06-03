using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.Runtime.CompilerServices;

namespace ManufacturingAI.Infrastructure.LLM;

// Wraps any ILLMService with Polly resilience: Timeout → Retry(2) → CircuitBreaker(3 failures/30 s).
public sealed class LLMCircuitBreakerService : ILLMService
{
    private static readonly LLMResponse Fallback = new(
        "The AI service is temporarily unavailable. Please refer to the original documents.",
        InputTokens: 0, OutputTokens: 0, IsFromFallback: true);

    private readonly ILLMService _inner;
    private readonly ILogger<LLMCircuitBreakerService> _logger;
    private readonly ResiliencePipeline<LLMResponse> _pipeline;
    private readonly CircuitBreakerStateProvider _stateProvider = new();

    public LLMCircuitBreakerService(ILLMService inner, ILogger<LLMCircuitBreakerService> logger)
    {
        _inner = inner;
        _logger = logger;
        _pipeline = BuildPipeline();
    }

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async token => await _inner.CompleteAsync(request, token), ct);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "LLM circuit breaker open — returning fallback.");
            return Fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM call failed after retries — returning fallback.");
            return Fallback;
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // If the circuit is already open, emit the fallback message immediately.
        if (_stateProvider.CircuitState is CircuitState.Open or CircuitState.Isolated)
        {
            _logger.LogWarning("LLM circuit breaker open — streaming fallback.");
            yield return Fallback.Content;
            yield break;
        }

        await foreach (var chunk in _inner.StreamAsync(request, ct))
            yield return chunk;
    }

    private ResiliencePipeline<LLMResponse> BuildPipeline() =>
        new ResiliencePipelineBuilder<LLMResponse>()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30)
            })
            .AddRetry(new RetryStrategyOptions<LLMResponse>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<LLMResponse>()
                    .Handle<Exception>(ex => ex is not BrokenCircuitException)
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<LLMResponse>
            {
                // Open after 3 failures in a 60-second window; stays open for 30 seconds.
                MinimumThroughput = 3,
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30),
                StateProvider = _stateProvider,
                ShouldHandle = new PredicateBuilder<LLMResponse>().Handle<Exception>(),
                OnOpened = args =>
                {
                    _logger.LogError("LLM circuit breaker opened. Break duration: {Duration}",
                        args.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation("LLM circuit breaker closed.");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
}
