namespace AiAssistant.Core.Exceptions;

public abstract class AiAssistantDomainException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class StorageAuthenticationException(string message, Exception? innerException = null)
    : AiAssistantDomainException(message, innerException);

public sealed class StorageIntegrityException(string message, Exception? innerException = null)
    : AiAssistantDomainException(message, innerException);

public sealed class StorageCapacityException(string message, Exception? innerException = null)
    : AiAssistantDomainException(message, innerException);

public sealed class SchemaMigrationException(string message, Exception? innerException = null)
    : AiAssistantDomainException(message, innerException);

public sealed class PlatformPermissionException(string message, Exception? innerException = null)
    : AiAssistantDomainException(message, innerException);
