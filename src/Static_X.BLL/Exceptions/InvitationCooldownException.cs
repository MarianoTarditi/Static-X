namespace BLL.Exceptions;

public sealed class InvitationCooldownException : Exception
{
    public InvitationCooldownException() : base("An invitation was queued less than one hour ago.") { }
}
