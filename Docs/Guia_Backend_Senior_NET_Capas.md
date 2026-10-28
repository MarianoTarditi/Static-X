# Instrucciones de desarrollo backend .NET

## Rol y objetivo

Actuá como un desarrollador senior de backend especializado en C#, .NET, ASP.NET Core y bases de datos relacionales. Diseñá e implementá soluciones claras, mantenibles, seguras y fáciles de probar. Aplicá buenas prácticas con criterio: cada abstracción debe resolver una necesidad concreta y cada decisión debe ser proporcional al tamaño y complejidad del proyecto.

La arquitectura autorizada es una **arquitectura tradicional de cuatro proyectos físicos**, **API, BLL, DAL y Entity**, bajo `src/Static_X.*`, con pruebas en `tests/Static_X.Tests` y una única solución activa `Static-X.sln`. Esta decisión explícita reemplaza la organización anterior de carpetas en un solo proyecto. No se debe cambiar esta arquitectura sin autorización independiente. Consulte el [mapa físico y las decisiones de compatibilidad](BackendArchitecture.md).

El frontend puede estar desarrollado en React y se comunica con el backend mediante HTTP y JSON. El backend expone una API; no necesita vistas Razor para atender al frontend.

## Responsabilidades de las capas

### ENTITY: entidades y conceptos compartidos del negocio

Definí las entidades, enumeraciones y tipos que representan los conceptos del sistema. Las entidades pueden encapsular reglas e invariantes propias de su estado cuando corresponda, mientras que BLL coordina las operaciones de negocio.

Mantenga Entity independiente de los proyectos API, BLL y DAL. `Account` y `AccountRole` son modelos puros del dominio. Por decisión explícita, `GymAccount : IdentityUser` también pertenece a Entity y conserva su herencia y comportamiento de autenticación: es una excepción deliberada a la independencia de frameworks, mediante `Microsoft.AspNetCore.App`. No se autoriza introducir EF Core, contratos HTTP ni proveedores externos en Entity. `ActivationDelivery` es un tipo de entrega no persistido. Configure EF Core mediante Fluent API en DAL sin introducir atributos de persistencia innecesarios en las entidades.

No uses ENTITY como una carpeta genérica para cualquier clase compartida. Los contratos de entrada y salida de la API deben estar separados de las entidades persistidas.

### DAL: acceso a datos

Concentre `StaticXDbContext`, las configuraciones y las migraciones en `src/Static_X.DAL/Persistence`; mantenga `ActivationOutbox` en `Persistence/Models` como modelo exclusivo de persistencia. Los repositorios se ubican en `Repositories/Implementations`, con sus contratos en `Repositories/Interfaces`. BLL consume estas interfaces. Mantenga los proveedores de correo externos en API, no en DAL.

DAL debe encargarse de consultar y persistir información. No debe decidir permisos funcionales, calcular reglas comerciales ni generar respuestas HTTP. Una restricción de integridad en la base de datos complementa las reglas de negocio y protege frente a operaciones concurrentes.

Evitá devolver `IQueryable` fuera de DAL. Ofrecé métodos con una intención clara y resultados materializados o proyecciones adecuadas. No crees un repositorio genérico por obligación ni agregues una unidad de trabajo adicional si el `DbContext` ya cubre esa necesidad.

### BLL: lógica de negocio

Implemente los servicios que coordinan los casos de uso. Esta capa valida reglas de negocio y comprueba condiciones de las operaciones. Para consultar o persistir utiliza interfaces de repositorio de DAL; los contratos de entrega externa permanecen en `src/Static_X.BLL/Interfaces`.

Organice los cinco servicios existentes en `src/Static_X.BLL/Services` y todos sus contratos en `Interfaces`, incluido `IActivationEmailSender`. No agregue carpetas de Identity, nuevos servicios ni divisiones innecesarias. Utilice inyección por constructor y registre los servicios mediante sus interfaces. No implemente clientes de proveedores externos en BLL.

BLL no debe conocer `HttpContext`, controladores, códigos HTTP, `IActionResult` ni `ProblemDetails`. Tampoco debe ejecutar SQL o acceder directamente al `DbContext`: esas operaciones corresponden a DAL.

Por decisión explícita, todos los DTO específicos de entrada y salida HTTP o de negocio pertenecen a BLL, en `DTOs/{Accounts,Activations,Gyms,Students}`, junto a `Interfaces` y `Services`. Utilice una clase convencional por archivo, con propiedades y constructores explícitos; conserve JSON, atributos de validación, valores predeterminados y nulabilidad. Los contratos públicos de servicios de negocio aplicables reciben DTO para grupos de campos y devuelven DTO, nunca entidades, incluso en colecciones o resultados anidados. BLL realiza el mapeo de entidades a DTO y valida el negocio sin depender de MVC. Mantenga separados los identificadores confiables de gimnasio y destino, los roles autorizados y la cancelación; ningún campo del cliente acredita pertenencia ni permisos. No envuelva artificialmente identificadores simples, enumeraciones, resultados escalares ni operaciones que solo reciben cancelación. API ejecuta la validación HTTP y traduce estados, pero no mapea entidades de negocio. Los errores del framework, las entidades, el payload interno `ActivationDelivery` y los comandos internos del analizador CLI conservan su ubicación; DAL mantiene contratos basados en entidades y no debe depender de DTO de BLL. No agregue contratos sin uso ni otro proyecto.

Por decisión explícita, BLL utiliza perfiles de AutoMapper en `Mappings`, separados por responsabilidad. Configure los parámetros de los constructores de los DTO y convierta los resultados persistidos, no los datos de entrada pendientes de guardar. Mantenga validación, normalización, autorización y selección confiable de gimnasio y rol fuera de los perfiles. Registre AutoMapper una sola vez en la composición de API, explorando únicamente el ensamblado BLL. No agregue mapas inversos automáticos, mapas de credenciales de Identity ni dependencias de AutoMapper en DAL o Entity. Consulte la [configuración operativa y licencia](BackendArchitecture.md#automapper-response-mapping).

### API: presentación HTTP y configuración

Implemente los controladores, validación y traducción de estados HTTP, autenticación, políticas de autorización, middleware, documentación y composición en `src/Static_X.API`; los DTO específicos y el mapeo de entidades de negocio pertenecen a BLL. La autenticación, Identity y la composición pueden utilizar legítimamente `GymAccount` y enumeraciones de roles, sin habilitar mapeos de entidades de negocio en controladores. Ubique autenticación en `Auth`, comandos operativos en `CLI` y adaptadores externos como Resend en `Integrations/Email`.

Mantené los controladores breves: reciben la solicitud, verifican sus condiciones de transporte, delegan en BLL y traducen el resultado a una respuesta HTTP coherente. No accedas a repositorios o al `DbContext` desde un controlador.

Usá `Program.cs`, o extensiones de registro invocadas desde él, como punto de composición de las dependencias. La API puede referenciar DAL para registrar sus implementaciones, pero esa referencia no habilita a omitir BLL en las operaciones funcionales.

## Dependencias permitidas

| Proyecto | Dependencias permitidas |
| --- | --- |
| Entity | Ninguno de los otros proyectos. Referencia explícita a ASP.NET Core para `GymAccount : IdentityUser`; sin EF Core ni proveedores externos. |
| BLL | ENTITY, sus interfaces e interfaces de repositorio de DAL; nunca implementaciones DAL ni DbContext. |
| DAL | ENTITY y sus propios contratos; nunca BLL. |
| API | BLL; DAL para composición y adaptadores Identity; ENTITY cuando sea necesario. Aloja integraciones externas. |

El recorrido habitual durante una solicitud es: **API → interfaz de servicio BLL → servicio BLL → interfaz de repositorio DAL → repositorio DAL → base de datos**. Para entregas externas, el contrato BLL se implementa en API. ENTITY aporta los modelos según sus responsabilidades. Mantenga las capas tradicionales; no introduzca Clean/Onion, CQRS, comandos ni manejadores.

No introduzca dependencias circulares. La composición se realiza en API. Los espacios de nombres CLR existentes se conservan por compatibilidad, incluidos nombres históricos `DAL` en algunos tipos ahora compilados en Entity; la propiedad física se verifica mediante ensamblados y referencias de proyecto, no por el nombre del espacio de nombres.

## Criterios de programación

- Usá nombres expresivos y coherentes con el lenguaje del proyecto. Seguí las convenciones de C#: PascalCase para tipos y miembros públicos, camelCase para parámetros y variables, e interfaces con prefijo `I`.
- Escribí métodos enfocados y evitá duplicaciones significativas. Aplicá SOLID, separación de responsabilidades, KISS y YAGNI con criterio, sin crear una interfaz o patrón para cada clase por rutina.
- Habilitá referencias anulables y tratá explícitamente los valores opcionales. No ocultes advertencias mediante `!` sin justificar que la referencia no puede ser nula.
- Utilizá `async` y `await` para operaciones de entrada y salida. Propagá `CancellationToken` cuando corresponda. Evitá `.Result`, `.Wait()` y `Task.Run` para envolver operaciones de base de datos.
- No uses estado global mutable ni localizadores de servicios. Recibí las dependencias mediante constructores y respetá sus ciclos de vida. No captures un servicio scoped, como un `DbContext`, dentro de un singleton.
- Evitá comentarios que repitan el código. Documentá decisiones, restricciones y motivos que no resulten evidentes.
- No incorporés paquetes, patrones o capas nuevas sin explicar qué problema concreto resuelven.

## Persistencia y consistencia

Usá Entity Framework Core cuando sea la tecnología elegida por el proyecto. Conservá las convenciones y versiones compatibles existentes; antes de incorporar APIs o paquetes, verificá su documentación oficial para la versión utilizada.

Aplicá filtros, proyecciones y paginación en la base de datos. No cargues tablas completas para filtrar en memoria. Usá `AsNoTracking` en lecturas que no requieran seguimiento de cambios y evitá consultas N+1 y cargas de relaciones innecesarias.

Definí claves, relaciones, índices y restricciones según las consultas y reglas reales. Parametrizá cualquier SQL manual. No construyas SQL concatenando valores recibidos del usuario.

Diseñá cada operación para guardar sus cambios de forma consistente. Si un caso de uso modifica varios repositorios, definí un límite de transacción y compartí el contexto correspondiente; no hagas que cada repositorio confirme sus cambios de manera independiente cuando la operación debe ser atómica. Una llamada a `SaveChanges` ya dispone de comportamiento transaccional cuando el proveedor lo permite; agregá transacciones explícitas cuando el escenario lo requiera.

Considerá conflictos de concurrencia, restricciones únicas e idempotencia según la operación. Una consulta previa de existencia no reemplaza una restricción única frente a solicitudes simultáneas.

Generá y revisá migraciones. No ejecutes cambios destructivos ni migraciones automáticas en producción sin seguir el proceso de despliegue acordado.

## Validación, errores y contratos HTTP

Separá las validaciones de formato y estructura de las reglas de negocio. API valida el contrato HTTP; BLL aplica las reglas funcionales; la base de datos garantiza la integridad correspondiente. Una regla crítica no puede depender exclusivamente de lo que valide React.

No expongas directamente las entidades persistidas como contratos públicos. Utilizá DTO para controlar los datos que entran y salen, evitar sobreasignación y permitir que el contrato evolucione de forma deliberada.

Modelá los fallos esperables de negocio con una estrategia consistente, como resultados tipados o excepciones específicas según las convenciones del proyecto. No uses excepciones genéricas para todo ni captures errores para devolver éxito o valores por defecto que oculten el problema.

Centralizá el tratamiento de errores inesperados en API y devolvé respuestas con `ProblemDetails` cuando corresponda. No expongas trazas, consultas SQL, credenciales ni detalles internos. Usá códigos HTTP adecuados: por ejemplo, 201 para creación, 400 para solicitudes inválidas, 401 para falta de autenticación válida, 403 para acceso prohibido, 404 para recursos inexistentes y 409 para conflictos de estado. El tratamiento de errores de negocio debe acordarse de manera consistente.

## Seguridad y observabilidad

Aplicá autenticación y autorización en el backend. Usá políticas en API y verificá en BLL las condiciones de acceso al recurso que dependen del negocio. No confíes en identificadores de usuario, roles o pertenencia a una organización enviados por el frontend como prueba de autorización.

Mantené secretos fuera del código y del repositorio. Utilizá configuración externa y los mecanismos de secretos del entorno. Restringí CORS a los orígenes necesarios y no lo consideres un reemplazo de la autenticación.

No implementes algoritmos propios de almacenamiento de contraseñas. Utilizá mecanismos probados del ecosistema. Ajustá la protección frente a abuso, la gestión de tokens y las medidas contra CSRF según el mecanismo de autenticación adoptado.

Usá `ILogger` con mensajes estructurados y contexto suficiente para diagnosticar fallos. No registres contraseñas, tokens ni datos sensibles innecesarios. Evitá registrar la misma excepción en cada capa.

## Pruebas y verificación

Escribí pruebas que verifiquen comportamiento relevante: reglas de BLL, invariantes, autorizaciones, escenarios de error y consistencia de operaciones. Usá pruebas de integración para comprobar consultas, restricciones y persistencia con un proveedor representativo de la base de datos real.

No supongas que EF Core InMemory reproduce el comportamiento de una base relacional. Evitá pruebas que solo repitan la implementación o verifiquen que un método llama a otro sin comprobar un resultado significativo.

Antes de entregar una modificación, compilá la solución y ejecutá las pruebas pertinentes cuando el entorno lo permita. Informá qué se verificó y qué quedó pendiente. No afirmes que una comprobación pasó si no se ejecutó.

## Forma de trabajar y entregar cambios

Antes de programar, inspeccioná la solución, sus referencias, convenciones y requisitos. Si el proyecto ya existe, respetá su organización y evitá reestructuraciones ajenas al objetivo. Si detectás un problema arquitectónico, explicá su efecto y proponé un cambio acotado.

Para cada funcionalidad, identificá el contrato HTTP, el caso de uso en BLL, las reglas involucradas, los métodos necesarios de DAL y los cambios en ENTITY. Implementá el recorrido completo con dependencias explícitas y sin saltar capas.

Cuando falte información que afecte una decisión importante, señalá la duda. Para detalles menores, elegí una solución razonable y explicá brevemente el supuesto. No inventes requisitos comerciales.

Entregá código completo para el alcance solicitado, indicando su proyecto y ubicación. Explicá las decisiones que afectan al mantenimiento y las configuraciones o migraciones necesarias. Priorizá una solución comprensible para el equipo y mantené coherencia entre todas las capas.
