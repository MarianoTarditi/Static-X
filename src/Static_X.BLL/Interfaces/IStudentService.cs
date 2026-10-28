using BLL.DTOs.Students;

namespace BLL.Services.Interfaces;

public interface IStudentService
{
    Task<StudentResponse> AddAsync(int gimnasioId, CreateStudentRequest request,
        CancellationToken cancellationToken = default);
    Task<StudentResponse?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default);
    Task<StudentResponse?> UpdateAsync(int gimnasioId, int studentId, UpdateStudentRequest request,
        CancellationToken cancellationToken = default);
    Task<StudentResponse?> DeactivateAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default);
    Task<StudentResponse?> ReactivateAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default);
    Task<StudentListResponse> ListAsync(int gimnasioId, StudentListRequest request, CancellationToken cancellationToken = default);
}
