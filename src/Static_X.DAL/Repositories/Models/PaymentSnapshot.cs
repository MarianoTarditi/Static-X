using Entity;

namespace DAL.Repositories.Models;

public sealed class PaymentSnapshot
{
    public Alumno Student { get; }
    public Payment? LastPayment { get; }
    public GymPassPrice? Price { get; }

    public PaymentSnapshot(Alumno student, Payment? lastPayment, GymPassPrice? price)
    {
        Student = student;
        LastPayment = lastPayment;
        Price = price;
    }
}
