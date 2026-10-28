namespace BLL.DTOs.Gyms;

/// <summary>Data for creating a gym through the business service.</summary>
public sealed class CreateGymRequest
{
    public string Name { get; init; }

    public CreateGymRequest(string name)
    {
        Name = name;
    }
}
