# Funcionamiento actual del módulo de usuarios

Documento técnico de referencia para reproducir el comportamiento actual de `agustinturriedf/web` en otro sistema. Describe lo observado en el código; no constituye una propuesta de cambios ni implica que todos los comportamientos actuales deban conservarse sin revisión.

## Resumen del flujo

1. Un administrador o entrenador autorizado emite una invitación. **«Crear alumno» en `/usuarios` no crea una cuenta:** envía `POST /api/invitations`.
2. La invitación contiene un enlace que vence a las 48 horas. La entrega del correo se procesa por separado.
3. El destinatario intercambia el token del enlace por una sesión de activación mediante `/activation/exchange?token=…`.
4. Al establecer su contraseña mediante `POST /api/activations`, se consume la invitación y se crea el usuario. Para alumnos también se crean el perfil, el pago inicial y el trabajo de provisión de rutina.
5. Las cuentas existentes se consultan y administran mediante `/api/users`. Archivar es un cambio de estado, no un borrado físico.

## Modelo y responsabilidades

| Entidad | Responsabilidad y campos relevantes |
| --- | --- |
| `User` | Identidad, email único, hash de contraseña, rol `ADMIN`/`TRAINER`/`STUDENT`, estado de **cuenta** `ACTIVE`/`INACTIVE`/`BLOCKED`/`ARCHIVED`, `version`, `authVersion` y datos de archivo. |
| `StudentProfile` | Relación opcional 1:1 con `User`, entrenador asignado nullable, estado **del alumno** `ACTIVE`/`INACTIVE`/`BLOCKED` y relaciones de pago/rutina. Su estado es independiente del estado de cuenta. |
| `Invitation` | Identidad previa al alta, rol, entrenador, hash del token, expiración, versión, estado de invitación `PENDING`/`SENT`/`CONSUMED`/`EXPIRED`/`REVOKED` y estado **de entrega** `PENDING`/`SENT`/`FAILED`. |
| `ActivationSession` | Hash de la sesión temporal obtenida al intercambiar el token del enlace. |
| `CurrentPayment` | Monto, deuda, fechas de inicio y vencimiento, y versión del ciclo de pago del alumno. |

El DTO de usuario incluye datos personales, contacto, rol, estado de cuenta, versión, fechas y un perfil de alumno reducido a identificador, entrenador y estado. El sobre habitual de API es `{ "success": true, "data": ... }` o `{ "success": false, "error": { "message": "...", "code": "...", "details": ... } }`.

Referencias: `agustinturriedf/web/prisma/schema.prisma`, `agustinturriedf/web/features/users/dto.ts`, `agustinturriedf/web/lib/http/api-response.ts`.

## Pantallas de administración

### `/usuarios`

- Solicita por separado `GET /api/users`, `GET /api/invitations` y `GET /api/payments/config`. Combina los resultados; la búsqueda, el ordenamiento y la paginación se ejecutan en el cliente, con **cuatro filas por página**.
- Para las filas de invitaciones, selecciona la más reciente por email. Descarta las que corresponden a una cuenta ya existente, no tienen entrega `SENT`, fueron consumidas o fueron revocadas. Clasifica como vencida una invitación por `expiresAt` incluso si su estado persistido aún no dice `EXPIRED`. Las métricas y filas visibles no equivalen necesariamente a todos los registros guardados.
- «Crear alumno» muestra el monto configurado como dato de consulta, pero no lo envía en el payload. Selecciona **el primer entrenador activo entre las filas cargadas**; no hay selector de entrenador. Envía una invitación `STUDENT` con `trainerId` y fecha inicial de pago, y luego vuelve a cargar los listados.
- «Archivar» ejecuta `PATCH /api/users/{id}/transitions` con control de versión y retira la fila al confirmar el resultado. «Revocar» y «Reenviar» actúan sobre invitaciones, sujetos a las reglas del servidor.
- El formulario «Editar usuario» es visual: actualmente su envío solo evita el comportamiento predeterminado y «Guardar Cambios» no tiene una mutación conectada. **No debe describirse como edición funcional de esta pantalla.**

La validación del formulario de creación exige teléfono y limita altura a 100–250 cm y peso a 30–350 kg; la validación de la API permite teléfono nullable y rangos más amplios, 50–300 cm y 20–500 kg. Además, un entrenador que recibe únicamente alumnos en `GET /api/users` puede no encontrar un entrenador activo entre las filas para usar este formulario, aunque la API sí le permita invitar alumnos propios. Son diferencias de implementación a considerar si se reproduce esta interfaz.

Referencias: `agustinturriedf/web/app/(private)/usuarios/page.tsx`, `agustinturriedf/web/app/(private)/usuarios/runtime.ts`, `agustinturriedf/web/lib/validation/invitations.ts`.

### `/usuarios/estudiantes`

Es un directorio distinto: usa la consulta paginada del servidor con `view=students`, filtros de nombre/email, estado de cuenta y `trainerId`, páginas de hasta 25 resultados y cursores. Ofrece una consulta explícita de archivados. No utiliza la paginación local de cuatro filas de `/usuarios`.

Referencia: `agustinturriedf/web/app/(private)/usuarios/estudiantes/page.tsx`.

## Contratos HTTP

Las operaciones de administración exigen sesión vigente y rol `ADMIN` o `TRAINER`, además de la política específica del recurso. `Público` indica que no se requiere sesión de administrador. Las consultas paginadas retornan `{ items, nextCursor }`; los listados sin paginación retornan arrays dentro del sobre de API.

### Usuarios

| Método y ruta | Acceso y contrato actual |
| --- | --- |
| `GET /api/users` | `ADMIN` ve todos los roles; `TRAINER`, solo `STUDENT`. Sin query devuelve un array, incluidos archivados. Con query requiere `view=students\|team` y admite filtros, orden, `limit` 1–100 y `cursor`; el directorio sin filtro de estado excluye archivados. `TRAINER` no puede usar `team`. |
| `POST /api/users` | Responde **405** (`Allow: GET`). Existe un método interno de creación, pero esta ruta HTTP no lo utiliza. |
| `GET /api/users/{userId}` | `ADMIN` puede consultar cualquier cuenta; `TRAINER`, cuentas `STUDENT` según la política actual. Retorna DTO de usuario. |
| `PATCH /api/users/{userId}` | Modifica `firstName`, `lastName` o `email`; exige `expectedVersion` y al menos un cambio. No edita rol, teléfono ni biometría por esta vía. La pantalla `/usuarios` no lo invoca al «guardar» su modal. |
| `PATCH /api/users/{userId}/assignment` | Asigna `trainerId` a un alumno con `expectedVersion`. Exige que el entrenador destino exista y esté activo; registra auditoría. |
| `PATCH /api/users/{userId}/transitions` | Recibe `targetStatus`, `expectedVersion` y `reason` de 1–500 caracteres; retorna DTO actualizado. Es la vía que usa «Archivar». |
| `GET /api/users/{userId}/audit` | Retorna eventos de auditoría paginados; `limit` 1–100, por defecto 25. |
| `POST /api/users/{userId}/password-reset` | Acepta la solicitud con respuesta genérica `202` y encola un correo de restablecimiento con token de 30 minutos; aplica limitación de frecuencia. |
| `DELETE /api/users/{userId}` | Borrado físico deshabilitado por HTTP: **405 `PHYSICAL_DELETE_DISABLED`**. |

Referencias: `agustinturriedf/web/app/api/users/`, `agustinturriedf/web/features/users/service.ts`, `agustinturriedf/web/features/users/repository.ts`, `agustinturriedf/web/lib/validation/users.ts`.

### Invitaciones y activación

| Método y ruta | Acceso y contrato actual |
| --- | --- |
| `GET /api/invitations` | `ADMIN` consulta todas; `TRAINER`, solo invitaciones `STUDENT` asociadas a sí mismo. Sin query devuelve DTOs seguros; con filtros y cursor devuelve `{ items, nextCursor }`. El DTO de listado **no incluye token**. |
| `POST /api/invitations` | `ADMIN` invita cualquier rol; `TRAINER` solo `STUDENT` propio. Acepta identidad, email, rol (por defecto `STUDENT`) y datos opcionales de perfil; para alumnos requiere entrenador activo y fecha inicial de pago. Responde `201` con DTO seguro, encola correo y **no crea aún el usuario**. |
| `GET /api/invitations/{invitationId}` | Lee una invitación según el alcance del actor. **Actualmente retorna el registro completo, no el DTO seguro del listado**; revisar su exposición antes de replicarlo. |
| `POST /api/invitations/{invitationId}/resend` | Recibe `expectedVersion`; rota token, restablece estado/entrega `PENDING`, fija una nueva expiración a 48 horas y encola correo. Admite invitaciones `PENDING`, `SENT` o `EXPIRED` si pasan las comprobaciones del servidor. |
| `DELETE /api/invitations/{invitationId}/revoke` | Recibe `expectedVersion` y `reason`; revoca solo una invitación `PENDING` o `SENT` no vencida e invalida su token. |
| `POST /api/invitations/replacement-requests` | Público; recibe email y responde genéricamente `202` para no revelar identidad ni confirmar que se envió un correo. Si corresponde, rota el token y encola nuevas instrucciones. |
| `GET /activation/exchange?token=…` | Público; intercambia el token por una cookie `activation_session` HttpOnly, SameSite Lax y Secure, válida por 10 minutos, y redirige a `/activation`. |
| `POST /api/activations` | Público con cookie de activación; recibe `password` y `confirmPassword`, consume sesión e invitación y crea la cuenta. La contraseña exige ocho caracteres, mayúscula, minúscula y número. |

Referencias: `agustinturriedf/web/app/api/invitations/`, `agustinturriedf/web/app/api/activations/route.ts`, `agustinturriedf/web/app/(auth)/activation/exchange/route.ts`, `agustinturriedf/web/features/invitations/service.ts`, `agustinturriedf/web/features/invitations/activation.ts`.

En general, un payload inválido da `400 VALIDATION_ERROR`; falta de acceso, `403 FORBIDDEN`; recurso inexistente, `404 NOT_FOUND`; conflicto de email, estado o versión, `409` con el código correspondiente. Las rutas de activación emplean además `INVALID_ACTIVATION` para tokens o sesiones inválidos. Los cursores del directorio están ligados a filtros/orden y requieren configuración de secreto.

## Reglas de estado y acceso

### Ciclo de cuenta

```text
ACTIVE   → INACTIVE | BLOCKED | ARCHIVED
INACTIVE → ACTIVE   | BLOCKED | ARCHIVED
BLOCKED  → ACTIVE   | ARCHIVED
ARCHIVED → INACTIVE
```

Restaurar un archivado **no** lo devuelve directamente a `ACTIVE`. Cada transición exige versión esperada y motivo, incrementa `version` y `authVersion`, modifica metadatos de archivo cuando corresponde y registra auditoría. No se permite que un `ADMIN` se archive a sí mismo ni dejar al sistema sin administradores activos. Al desactivar un entrenador activo se desasignan sus alumnos en la transacción. La pantalla principal solo ofrece la acción de archivar, no todas estas transiciones.

Referencia: `agustinturriedf/web/features/users/repository.ts`.

### Invitación, correo y pago

- La invitación inicial usa un hash de token y expira a las 48 horas. **Estado de invitación y estado de entrega son campos diferentes.** El worker verifica token y vigencia antes del envío; no debe suponerse que la entrega cambie automáticamente el estado de invitación a `SENT`, ni que el paso del tiempo materialice `EXPIRED` en la base.
- Reenvío y solicitud de reemplazo rotan el token anterior. La revocación borra su hash. El cliente también puede calcular el vencimiento comparando `expiresAt` con la hora actual.
- En la activación HTTP de un alumno, el pago inicial toma el monto de la configuración global, parte con deuda igual a ese monto y calcula su vencimiento 30 días después de la fecha inicial normalizada a Buenos Aires. El método interno de creación directa de usuario **no es equivalente** a este flujo de activación.
- El inicio de sesión y las comprobaciones de sesión niegan acceso a cuentas no activas y consideran el estado del alumno y el pago vencido. `NO_CYCLE` (sin ciclo de pago) no equivale automáticamente a deuda vencida. La sesión JWT transporta rol y datos de alumno/pago, pero la comprobación de acceso vuelve a consultar información actual.

Referencias: `agustinturriedf/web/features/outbox/worker.ts`, `agustinturriedf/web/features/invitations/activation.ts`, `agustinturriedf/web/features/payments/repository.ts`, `agustinturriedf/web/features/payments/timezone.ts`, `agustinturriedf/web/features/auth/credentials.ts`, `agustinturriedf/web/features/auth/session.ts`, `agustinturriedf/web/features/auth/payment-access.ts`.

## Precauciones al implementarlo en otro sistema

1. **Separar cuenta, perfil de alumno, invitación, entrega y pago.** Reducirlos a un único «estado del usuario» pierde reglas reales del sistema.
2. **Distinguir API de interfaz.** `POST /api/users` y `DELETE /api/users/{id}` no están disponibles; el modal de edición de `/usuarios` no guarda, aunque `PATCH /api/users/{id}` sí exista.
3. **Revisar permisos de `TRAINER` antes de copiarlos.** La política actual permite consultar y mutar cuentas `STUDENT` sin comprobar necesariamente que estén asignadas al entrenador; las invitaciones sí restringen el alcance al entrenador propio.
4. **Revisar la exposición de `GET /api/invitations/{id}`.** A diferencia del listado, devuelve el registro completo, cuyo modelo contiene `tokenHash`. No trasladar ese contrato a otro cliente sin una decisión de seguridad explícita.
5. **Resolver la selección de entrenador.** La pantalla usa el primero activo entre los usuarios cargados, lo que puede fallar para un actor `TRAINER` que solo recibe alumnos en el listado.

Este documento describe el código inspeccionado; no se ejecutaron pruebas para elaborarlo.
