namespace BLL.DTOs.Gyms;

/// <summary>Created gym identity and name.</summary>
public sealed class GymResponse
{
    public int Id { get; }
    public string Nombre { get; }

    public GymResponse(int id, string nombre)
    {
        Id = id;
        Nombre = nombre;
    }
}
