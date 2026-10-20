# Proyecto de gimnasio: Agustín Turri EDF

La aplicación de gimnasio de este repositorio busca centralizar la gestión de alumnos, cobros y materiales de entrenamiento en una plataforma privada. El repositorio también incluye una *landing* pública de presentación.

## Alcance y módulos

| Módulo | Qué cubre |
| --- | --- |
| **Acceso y perfil** | Inicio de sesión, activación mediante invitación, recuperación de contraseña y edición del perfil propio. |
| **Usuarios y alumnos** | Consulta de cuentas y alumnos, alta mediante invitación, asignación de entrenador, estados de cuenta y archivo de usuarios. Incluye reenvío y revocación de invitaciones. |
| **Pagos** | Seguimiento de ciclos, vencimientos y deudas; registro de cobros y configuración del importe mensual. |
| **Rutinas** | Organización de rutinas por alumno y gestión de archivos PDF y planillas. El alumno consulta sus propias rutinas. |
| **Videoteca** | Carpetas y archivos de imágenes o videos para compartir material de entrenamiento. |
| **Panel principal** | Vista de métricas y actividad, todavía de demostración: sus cifras y gráficos no provienen de datos operativos reales. |

El sistema contempla tres roles —**administrador, entrenador y alumno**— con permisos distintos. Además, el estado de la cuenta y las deudas vencidas intervienen en el acceso del alumno.

## Estado actual y límites

No todo lo que aparece en pantalla está terminado. Por ejemplo, el formulario de «Editar usuario» todavía no guarda cambios. También hay documentos de diseño con propuestas futuras, como almacenamiento privado en S3 para la videoteca, que no deben confundirse con funcionalidades ya implementadas.
