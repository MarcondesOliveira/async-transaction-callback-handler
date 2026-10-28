namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;

public sealed record TransactionProcessed(
    int EventVersion,
    string EventType,
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TransactionId,
    Guid CorrelationId,
    Guid ProcessorTransactionId,
    string Status,
    ProcessingResult Result);

public sealed record ProcessingResult(string Code, string Message);

public enum CallbackOutcome
{
    Applied,
    Duplicate
}

public interface ICallbackRepository
{
    Task<CallbackOutcome> ApplyAsync(
        TransactionProcessed callback,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken);
}

public sealed class CallbackConflictException(string message) : Exception(message);

public sealed class CallbackValidationException(string message) : Exception(message);
