using Entity;

namespace DAL.Repositories.Models;

public sealed class PaymentSnapshot
{
    public Student Student { get; }
    public Payment? LastPayment { get; }
    public GymPassPrice? Price { get; }

    public PaymentSnapshot(Student student, Payment? lastPayment, GymPassPrice? price)
    {
        Student = student;
        LastPayment = lastPayment;
        Price = price;
    }
}
