namespace DAL.Repositories.Models;

public sealed class InvitationCooldownException : Exception
{
    public InvitationCooldownException() : base("Invitation queue interval has not elapsed.") { }
}
