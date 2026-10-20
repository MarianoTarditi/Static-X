using System;
using System.Collections.Generic;
using System.Text;

namespace Entity
{
    public class Alumno
    {
        public int Id { get; set; }
        public int GimnasioId { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public string Apellido { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
