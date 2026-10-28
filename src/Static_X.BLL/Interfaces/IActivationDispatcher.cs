namespace BLL.Services.Interfaces;

public interface IActivationDispatcher
{
    Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default);
}
