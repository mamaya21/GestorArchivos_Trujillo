namespace GestorArchivos.Application.Common.Interfaces;

public interface ICurrentUserService
{
    Guid? UserId { get; }
    bool IsAuthenticated { get; }
}
