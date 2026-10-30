namespace DAL.Repositories.Exceptions;

/// <summary>Signals that a persisted provisioning key was reused with different inputs.</summary>
public sealed class ProvisioningConflictException : InvalidOperationException
{
    public ProvisioningConflictException() : base("Provisioning key belongs to a different request.")
    {
    }
}
