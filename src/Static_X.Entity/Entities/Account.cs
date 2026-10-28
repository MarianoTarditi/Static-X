namespace Entity;

public sealed class Account
{
    public string Id { get; }
    public int GymId { get; }
    public string Email { get; }
    public AccountRole Role { get; }

    public Account(string Id, int GymId, string Email, AccountRole Role)
    {
        this.Id = Id;
        this.GymId = GymId;
        this.Email = Email;
        this.Role = Role;
    }
}
