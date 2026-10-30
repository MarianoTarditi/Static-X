namespace BLL.DTOs.Activations;

/// <summary>Identifies the persisted gym of the successfully activated account.</summary>
public sealed class ActivationResponse
{
    public int GymId { get; }

    public ActivationResponse(int gymId)
    {
        GymId = gymId;
    }
}
