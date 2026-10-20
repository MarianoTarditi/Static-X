using DAL.Repositories.Interfaces;
using Entity;

namespace BLL;

// For trusted manual provisioning only; not an HTTP authorization mechanism.
public sealed class GymService(IGymRepository repository)
{
    public Task<Gimnasio> AddAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A gym name is required.", nameof(name));

        return repository.AddAsync(new Gimnasio { Nombre = name.Trim() }, cancellationToken);
    }
}
