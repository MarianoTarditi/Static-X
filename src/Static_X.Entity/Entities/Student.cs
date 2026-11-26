using System;
using System.Collections.Generic;
using System.Text;

namespace Entity
{
    public class Student
    {
        public int Id { get; set; }
        public int GymId { get; set; }
        public string? AccountId { get; set; }
        public long PaymentRevision { get; set; }
        public bool IsActive { get; set; } = true;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
