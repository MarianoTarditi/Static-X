namespace BLL.Exceptions;

/// <summary>Signals reuse of a provisioning key for a different business request.</summary>
public sealed class ProvisioningConflictException : InvalidOperationException
{
    public ProvisioningConflictException() : base("Provisioning key belongs to a different request.")
    {
    }
}
