using AutoMapper;
using BLL.Services.Interfaces;
using BLL.DTOs.Students;
using BLL.Exceptions;
using DAL.Repositories.Exceptions;
using DAL.Repositories.Interfaces;
using Entity;
using System.ComponentModel.DataAnnotations;

namespace BLL;

// Valida los datos de alumnos y coordina su alta y consulta por gimnasio
/// <summary>Valida alumnos y coordina su alta, modificación y consulta dentro de un gimnasio.</summary>
public sealed class StudentService : IStudentService
{
    private readonly IStudentRepository repository;
    private readonly IMapper mapper;

    /// <summary>Recibe el repositorio de alumnos y el mapeador de sus respuestas públicas.</summary>
    public StudentService(IStudentRepository repository, IMapper mapper)
    {
        this.repository = repository;
        this.mapper = mapper;
    }

    // Valida el gimnasio y los datos obligatorios, convierte el correo a mayúsculas y solicita guardar el alumno.
    /// <summary>Valida y normaliza los datos del alumno y traduce el conflicto de correo a un error de negocio.</summary>
    public async Task<StudentResponse> AddAsync(int gimnasioId, CreateStudentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        ArgumentNullException.ThrowIfNull(request);
        var normalizedEmail = NormalizeEmail(request.Email);
        var student = new Alumno
        {
            GimnasioId = gimnasioId,
            Activo = true,
            Nombre = Required(request.Nombre, "nombre"),
            Apellido = Required(request.Apellido, "apellido"),
            Email = normalizedEmail
        };
        try
        {
            return mapper.Map<StudentResponse>(await repository.AddAsync(student, cancellationToken));
        }
        catch (DuplicateStudentEmailException)
        {
            throw new StudentEmailConflictException();
        }
    }

    /// <summary>Valida y normaliza los campos editables y actualiza el alumno dentro de su gimnasio.</summary>
    public async Task<StudentResponse?> UpdateAsync(int gimnasioId, int studentId, UpdateStudentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(studentId);
        ArgumentNullException.ThrowIfNull(request);
        var normalizedEmail = NormalizeEmail(request.Email);
        var normalizedNombre = Required(request.Nombre, "nombre");
        var normalizedApellido = Required(request.Apellido, "apellido");
        try
        {
            var student = await repository.UpdateAsync(gimnasioId, studentId, normalizedNombre, normalizedApellido, normalizedEmail, cancellationToken);
            return student is null ? null : mapper.Map<StudentResponse>(student);
        }
        catch (DuplicateStudentEmailException)
        {
            throw new StudentEmailConflictException();
        }
    }

    /// <summary>Normaliza el correo y exige el mismo formato y longitud en altas y modificaciones.</summary>
    private static string NormalizeEmail(string email)
    {
        var normalizedEmail = Required(email, nameof(email)).ToUpperInvariant();
        if (normalizedEmail.Length > 320 || !new EmailAddressAttribute().IsValid(normalizedEmail))
            throw new ArgumentException("A valid email of at most 320 characters is required.", nameof(email));
        return normalizedEmail;
    }

    // Valida ambos identificadores antes de buscar el alumno dentro del gimnasio indicado.
    /// <summary>Valida los identificadores y busca un alumno únicamente dentro del gimnasio solicitado.</summary>
    public async Task<StudentResponse?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(studentId);
        var student = await repository.FindAsync(gimnasioId, studentId, cancellationToken);
        return student is null ? null : mapper.Map<StudentResponse>(student);
    }

    /// <summary>Valida y normaliza los filtros y límites de página y convierte las fichas persistidas a respuestas públicas.</summary>
    public async Task<StudentListResponse> ListAsync(int gimnasioId, StudentListRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Page);
        if (request.PageSize < 1 || request.PageSize > 100)
            throw new ArgumentOutOfRangeException(nameof(request.PageSize));
        if (request.Search?.Length > 320)
            throw new ArgumentException("Search must contain at most 320 characters.", nameof(request.Search));
        var offset = ((long)request.Page - 1) * request.PageSize;
        if (offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.Page), "The requested page offset is too large.");
        var search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim().ToUpperInvariant();
        var result = await repository.ListAsync(gimnasioId, request.Activo, search, (int)offset, request.PageSize, cancellationToken);
        return new StudentListResponse(mapper.Map<StudentResponse[]>(result.Items), request.Page, request.PageSize, result.TotalCount);
    }

    /// <summary>Desactiva la ficha del alumno sin eliminarla ni modificar sus datos personales.</summary>
    public Task<StudentResponse?> DeactivateAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default) =>
        SetActiveAsync(gimnasioId, studentId, false, cancellationToken);

    /// <summary>Reactiva la ficha del alumno dentro del gimnasio autorizado.</summary>
    public Task<StudentResponse?> ReactivateAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default) =>
        SetActiveAsync(gimnasioId, studentId, true, cancellationToken);

    /// <summary>Valida los identificadores y convierte el estado persistido a una respuesta pública.</summary>
    private async Task<StudentResponse?> SetActiveAsync(int gimnasioId, int studentId, bool active, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(studentId);
        var student = await repository.SetActiveAsync(gimnasioId, studentId, active, cancellationToken);
        return student is null ? null : mapper.Map<StudentResponse>(student);
    }

    // Rechaza valores vacíos e informa el campo afectado; devuelve el texto sin espacios en los extremos.
    /// <summary>Rechaza campos vacíos y elimina los espacios de sus extremos.</summary>
    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
}
