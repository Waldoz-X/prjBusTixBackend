using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using prjBusTix.Data;
using prjBusTix.Dto.Notificaciones;
using prjBusTix.Model;
using prjBusTix.Services;
using System.Security.Claims;

namespace prjBusTix.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificacionesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly INotificacionService _notificacionService;
    private readonly UserManager<ClApplicationUser> _userManager;
    private readonly ILogger<NotificacionesController> _logger;
    
    public NotificacionesController(
        AppDbContext context,
        INotificacionService notificacionService,
        UserManager<ClApplicationUser> userManager,
        ILogger<NotificacionesController> logger)
    {
        _context = context;
        _notificacionService = notificacionService;
        _userManager = userManager;
        _logger = logger;
    }
    
    /// <summary>
    /// Envía notificación broadcast (masiva)
    /// POST /api/notificaciones/broadcast
    /// </summary>
    [HttpPost("broadcast")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> EnviarBroadcast([FromBody] NotificacionBroadcastDto dto)
    {
        try
        {
            List<string> destinatarios = new List<string>();
            
            // Caso 1: Notificación a pasajeros de un viaje específico
            if (dto.ViajeID.HasValue)
            {
                var viaje = await _context.Viajes
                    .Include(v => v.PlantillaRuta)
                    .FirstOrDefaultAsync(v => v.ViajeID == dto.ViajeID.Value);
                
                if (viaje == null)
                    return NotFound(new { message = "Viaje no encontrado" });
                
                // Obtener IDs de clientes con boletos pagados en ese viaje
                var clientesIds = await (
                    from b in _context.Boletos
                    where b.ViajeID == dto.ViajeID.Value && b.Estatus == 10
                    select b.ClienteID
                ).Distinct().ToListAsync();
                
                destinatarios.AddRange(clientesIds);
                
                _logger.LogInformation(
                    "Broadcast a {Count} pasajeros del viaje {CodigoViaje}",
                    destinatarios.Count, viaje.CodigoViaje);
            }
            // Caso 2: Notificación por roles
            else if (dto.Roles != null && dto.Roles.Any())
            {
                foreach (var rol in dto.Roles)
                {
                    var usuariosEnRol = await _userManager.GetUsersInRoleAsync(rol);
                    destinatarios.AddRange(usuariosEnRol.Select(u => u.Id));
                }
                destinatarios = destinatarios.Distinct().ToList();
                
                var rolesString = string.Join(", ", dto.Roles);
                _logger.LogInformation(
                    "Broadcast a {Count} usuarios con roles: {Roles}",
                    destinatarios.Count, rolesString);
            }
            // Caso 3: IDs específicos
            else if (dto.UsuariosIDs != null && dto.UsuariosIDs.Any())
            {
                destinatarios = dto.UsuariosIDs;
                
                _logger.LogInformation(
                    "Broadcast a {Count} usuarios específicos",
                    destinatarios.Count);
            }
            else
            {
                return BadRequest(new { message = "Debe especificar ViajeID, Roles o UsuariosIDs" });
            }
            
            if (!destinatarios.Any())
            {
                return BadRequest(new { message = "No se encontraron destinatarios" });
            }
            
            // Crear notificaciones para cada destinatario
            var notificaciones = new List<Notificacion>();
            
            foreach (var usuarioId in destinatarios)
            {
                var notificacion = new Notificacion
                {
                    UsuarioID = usuarioId,
                    ViajeID = dto.ViajeID,
                    Titulo = dto.Titulo,
                    Mensaje = dto.Mensaje,
                    TipoNotificacion = dto.TipoNotificacion,
                    EnviarPush = dto.EnviarPush,
                    EnviarEmail = dto.EnviarEmail,
                    FechaCreacion = DateTime.Now
                };
                
                notificaciones.Add(notificacion);
            }
            
            _context.Notificaciones.AddRange(notificaciones);
            await _context.SaveChangesAsync();
            
            // Enviar notificaciones en segundo plano
            int exitosas = 0;
            foreach (var notif in notificaciones)
            {
                var resultado = await _notificacionService.EnviarNotificacionAsync(notif);
                if (resultado) exitosas++;
            }
            
            return Ok(new
            {
                message = "Notificaciones enviadas",
                totalDestinatarios = destinatarios.Count,
                notificacionesCreadas = notificaciones.Count,
                notificacionesEnviadas = exitosas
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar broadcast");
            return StatusCode(500, new { message = "Error al enviar notificaciones masivas" });
        }
    }
    
    /// <summary>
    /// Crea una notificación individual
    /// POST /api/notificaciones
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<NotificacionResponseDto>> CrearNotificacion([FromBody] CrearNotificacionDto dto)
    {
        try
        {
            var notificacion = new Notificacion
            {
                UsuarioID = dto.UsuarioID,
                ViajeID = dto.ViajeID,
                BoletoID = dto.BoletoID,
                Titulo = dto.Titulo,
                Mensaje = dto.Mensaje,
                TipoNotificacion = dto.TipoNotificacion,
                EnviarPush = dto.EnviarPush,
                EnviarEmail = dto.EnviarEmail,
                FechaCreacion = DateTime.Now
            };
            
            _context.Notificaciones.Add(notificacion);
            await _context.SaveChangesAsync();
            
            // Enviar la notificación
            await _notificacionService.EnviarNotificacionAsync(notificacion);
            
            return CreatedAtAction(
                nameof(ObtenerNotificacion),
                new { id = notificacion.NotificacionID },
                MapToDto(notificacion));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al crear notificación");
            return StatusCode(500, new { message = "Error al crear la notificación" });
        }
    }
    
    /// <summary>
    /// Obtiene el historial de notificaciones del usuario autenticado
    /// GET /api/notificaciones/me
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<IEnumerable<NotificacionResponseDto>>> MisNotificaciones(
        [FromQuery] bool? soloNoLeidas = null,
        [FromQuery] int pagina = 1,
        [FromQuery] int porPagina = 20)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            
            var query = _context.Notificaciones
                .Include(n => n.Viaje)
                .Include(n => n.Boleto)
                .Where(n => n.UsuarioID == userId);
            
            if (soloNoLeidas == true)
            {
                query = query.Where(n => !n.FueLeida);
            }
            
            var total = await query.CountAsync();
            
            var notificaciones = await query
                .OrderByDescending(n => n.FechaCreacion)
                .Skip((pagina - 1) * porPagina)
                .Take(porPagina)
                .ToListAsync();
            
            var response = notificaciones.Select(MapToDto).ToList();
            
            Response.Headers["X-Total-Count"] = total.ToString();
            Response.Headers["X-Total-Pages"] = ((int)Math.Ceiling(total / (double)porPagina)).ToString();
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener notificaciones del usuario");
            return StatusCode(500, new { message = "Error al obtener notificaciones" });
        }
    }
    
    /// <summary>
    /// Obtiene una notificación específica
    /// GET /api/notificaciones/{id}
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<NotificacionResponseDto>> ObtenerNotificacion(int id)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin = User.IsInRole("Admin") || User.IsInRole("Manager");
            
            var notificacion = await _context.Notificaciones
                .Include(n => n.Viaje)
                .Include(n => n.Boleto)
                .FirstOrDefaultAsync(n => n.NotificacionID == id);
            
            if (notificacion == null)
                return NotFound(new { message = "Notificación no encontrada" });
            
            // Verificar que pertenece al usuario o es admin
            if (!isAdmin && notificacion.UsuarioID != userId)
                return Forbid();
            
            return Ok(MapToDto(notificacion));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener notificación {NotificacionId}", id);
            return StatusCode(500, new { message = "Error al obtener la notificación" });
        }
    }
    
    /// <summary>
    /// Marca una notificación como leída
    /// PUT /api/notificaciones/{id}/marcar-leida
    /// </summary>
    [HttpPut("{id}/marcar-leida")]
    public async Task<IActionResult> MarcarComoLeida(int id)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            
            var notificacion = await _context.Notificaciones
                .FirstOrDefaultAsync(n => n.NotificacionID == id && n.UsuarioID == userId);
            
            if (notificacion == null)
                return NotFound(new { message = "Notificación no encontrada" });
            
            notificacion.FueLeida = true;
            notificacion.FechaLectura = DateTime.Now;
            
            await _context.SaveChangesAsync();
            
            return Ok(new { message = "Notificación marcada como leída" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al marcar notificación {NotificacionId} como leída", id);
            return StatusCode(500, new { message = "Error al actualizar la notificación" });
        }
    }
    
    /// <summary>
    /// Marca todas las notificaciones como leídas
    /// PUT /api/notificaciones/me/marcar-todas-leidas
    /// </summary>
    [HttpPut("me/marcar-todas-leidas")]
    public async Task<IActionResult> MarcarTodasComoLeidas()
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            
            var notificaciones = await _context.Notificaciones
                .Where(n => n.UsuarioID == userId && !n.FueLeida)
                .ToListAsync();
            
            var ahora = DateTime.Now;
            foreach (var notif in notificaciones)
            {
                notif.FueLeida = true;
                notif.FechaLectura = ahora;
            }
            
            await _context.SaveChangesAsync();
            
            return Ok(new
            {
                message = "Todas las notificaciones marcadas como leídas",
                cantidad = notificaciones.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al marcar todas las notificaciones como leídas");
            return StatusCode(500, new { message = "Error al actualizar las notificaciones" });
        }
    }
    
    /// <summary>
    /// Obtiene el conteo de notificaciones no leídas
    /// GET /api/notificaciones/me/no-leidas/count
    /// </summary>
    [HttpGet("me/no-leidas/count")]
    public async Task<ActionResult<int>> ConteoNoLeidas()
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            
            var count = await _context.Notificaciones
                .Where(n => n.UsuarioID == userId && !n.FueLeida)
                .CountAsync();
            
            return Ok(new { count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al contar notificaciones no leídas");
            return StatusCode(500, new { message = "Error al obtener el conteo" });
        }
    }
    
    /// <summary>
    /// Registra el token de dispositivo para notificaciones push
    /// POST /api/notificaciones/dispositivo
    /// </summary>
    [HttpPost("dispositivo")]
    public async Task<IActionResult> RegistrarDispositivo([FromBody] RegistrarDispositivoDto dto)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            
            // Verificar si el token ya existe
            var dispositivoExistente = await _context.DispositivosUsuario
                .FirstOrDefaultAsync(d => d.TokenPush == dto.TokenPush && d.UsuarioID == userId);
            
            if (dispositivoExistente != null)
            {
                // Actualizar dispositivo existente
                dispositivoExistente.EsActivo = true;
                dispositivoExistente.TipoDispositivo = dto.TipoDispositivo;
                dispositivoExistente.Plataforma = dto.Plataforma;
                dispositivoExistente.FechaRegistro = DateTime.Now;
            }
            else
            {
                // Crear nuevo dispositivo
                var nuevoDispositivo = new DispositivoUsuario
                {
                    UsuarioID = userId,
                    TokenPush = dto.TokenPush,
                    TipoDispositivo = dto.TipoDispositivo,
                    Plataforma = dto.Plataforma,
                    EsActivo = true,
                    FechaRegistro = DateTime.Now
                };
                
                _context.DispositivosUsuario.Add(nuevoDispositivo);
            }
            
            await _context.SaveChangesAsync();
            
            return Ok(new { message = "Dispositivo registrado correctamente" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar dispositivo");
            return StatusCode(500, new { message = "Error al registrar el dispositivo" });
        }
    }
    
    /// <summary>
    /// Desactiva un token de dispositivo
    /// DELETE /api/notificaciones/dispositivo/{token}
    /// </summary>
    [HttpDelete("dispositivo/{token}")]
    public async Task<IActionResult> DesactivarDispositivo(string token)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            
            var dispositivo = await _context.DispositivosUsuario
                .FirstOrDefaultAsync(d => d.TokenPush == token && d.UsuarioID == userId);
            
            if (dispositivo == null)
                return NotFound(new { message = "Dispositivo no encontrado" });
            
            dispositivo.EsActivo = false;
            await _context.SaveChangesAsync();
            
            return Ok(new { message = "Dispositivo desactivado" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al desactivar dispositivo");
            return StatusCode(500, new { message = "Error al desactivar el dispositivo" });
        }
    }
    
    // Método auxiliar
    private NotificacionResponseDto MapToDto(Notificacion notificacion)
    {
        return new NotificacionResponseDto
        {
            NotificacionID = notificacion.NotificacionID,
            Titulo = notificacion.Titulo,
            Mensaje = notificacion.Mensaje,
            TipoNotificacion = notificacion.TipoNotificacion,
            ViajeID = notificacion.ViajeID,
            CodigoViaje = notificacion.Viaje?.CodigoViaje,
            BoletoID = notificacion.BoletoID,
            CodigoBoleto = notificacion.Boleto?.CodigoBoleto,
            FueEnviada = notificacion.FueEnviada,
            FechaEnvio = notificacion.FechaEnvio,
            FueLeida = notificacion.FueLeida,
            FechaLectura = notificacion.FechaLectura,
            FechaCreacion = notificacion.FechaCreacion
        };
    }
}

