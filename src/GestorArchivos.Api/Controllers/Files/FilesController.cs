using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Files;

[ApiController]
[Authorize]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private const long MaxFileSize =
        4L * 1024 * 1024 * 1024;

    private readonly ApplicationDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IFolderPermissionService _permissionService;
    private readonly ISyncChangeService _syncChangeService;

    public FilesController(
        ApplicationDbContext dbContext,
        IFileStorage fileStorage,
        IFolderPermissionService permissionService,
        ISyncChangeService syncChangeService)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _permissionService = permissionService;
        _syncChangeService = syncChangeService;
    }

    public sealed record CreateEmptyFileRequest(Guid FolderId, string Name);

    [HttpPost("empty")]
    public async Task<IActionResult> CreateEmpty(
        [FromBody] CreateEmptyFileRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileNameValidator.TryValidate(request.Name, out var name, out var error))
            return BadRequest(new { message = error });

        if (!string.Equals(Path.GetExtension(name), ".txt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Solo se permite crear archivos .txt." });

        if (!await _dbContext.Carpetas.AsNoTracking().AnyAsync(
                x => x.Id == request.FolderId && !x.EnPapelera, cancellationToken))
            return BadRequest(new { message = "La carpeta no existe." });

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(), request.FolderId, PermisoCarpeta.Crear, cancellationToken))
            return Forbid();

        if (await _dbContext.Archivos.AsNoTracking().AnyAsync(
                x => x.CarpetaId == request.FolderId && x.Nombre == name &&
                     x.Estado == EstadoArchivo.Activo, cancellationToken))
            return Conflict(new { message = "Ya existe un archivo con ese nombre." });

        var id = Guid.NewGuid();
        var relativePath = Path.Combine(DateTime.UtcNow.ToString("yyyy"),
            DateTime.UtcNow.ToString("MM"), $"{id:N}.txt");

        await using (var empty = new MemoryStream())
            await _fileStorage.SaveAsync(relativePath, empty, cancellationToken);

        var entity = new Archivo
        {
            Id = id,
            Nombre = name,
            NombreFisico = relativePath,
            Extension = ".txt",
            TipoContenido = "text/plain",
            TamanoBytes = 0,
            HashSha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())),
            Version = 1,
            CarpetaId = request.FolderId,
            CreadoPorUsuarioId = CurrentUserId()
        };

        try
        {
            _dbContext.Archivos.Add(entity);
            await _syncChangeService.RegisterAsync(request.FolderId, entity.Id, null,
                "FILE_CREATED", entity.Version, cancellationToken: cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _fileStorage.DeleteAsync(relativePath, CancellationToken.None);
            throw;
        }

        return CreatedAtAction(nameof(GetById), new { id = entity.Id },
            new { entity.Id, entity.Nombre, entity.TamanoBytes, entity.TipoContenido });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] Guid folderId,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
            return BadRequest(new { message = "El archivo est� vac�o." });

        if (file.Length > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "El archivo supera el l�mite de 4 GB."
            });
        }

        var folderExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == folderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (!folderExists)
        {
            return BadRequest(new
            {
                message = "La carpeta no existe."
            });
        }

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                folderId,
                PermisoCarpeta.Crear,
                cancellationToken))
        {
            return Forbid();
        }

        if (!FileNameValidator.TryValidate(
                file.FileName,
                out var originalName,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        var duplicateExists = await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.CarpetaId == folderId &&
                    x.Nombre == originalName &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre."
            });
        }

        var id = Guid.NewGuid();
        var extension = Path.GetExtension(originalName);
        var physicalName = $"{id:N}{extension}";

        var relativePath = Path.Combine(
            DateTime.UtcNow.ToString("yyyy"),
            DateTime.UtcNow.ToString("MM"),
            physicalName);

        await using var source =
            file.OpenReadStream();

        using var sha256 = SHA256.Create();

        var hash = await sha256.ComputeHashAsync(
            source,
            cancellationToken);

        source.Position = 0;

        await _fileStorage.SaveAsync(
            relativePath,
            source,
            cancellationToken);

        var entity = new Archivo
        {
            Id = id,
            Nombre = originalName,
            NombreFisico = relativePath,
            Extension = extension,
            TipoContenido =
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType,
            TamanoBytes = file.Length,
            HashSha256 = Convert.ToHexString(hash),
            Version = 1,
            CarpetaId = folderId,
            CreadoPorUsuarioId = CurrentUserId()
        };

        try
        {
            _dbContext.Archivos.Add(entity);

            await _syncChangeService.RegisterAsync(
                folderId,
                entity.Id,
                null,
                "FILE_CREATED",
                entity.Version,
                cancellationToken: cancellationToken);

            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
        catch
        {
            await _fileStorage.DeleteAsync(
                relativePath,
                CancellationToken.None);

            throw;
        }

        return CreatedAtAction(
            nameof(GetById),
            new { id = entity.Id },
            new
            {
                entity.Id,
                entity.Nombre,
                entity.TamanoBytes,
                entity.TipoContenido,
                entity.Version,
                entity.FechaCreacion
            });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x =>
                x.Id == id &&
                x.Estado == EstadoArchivo.Activo)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.Extension,
                x.TipoContenido,
                x.TamanoBytes,
                x.HashSha256,
                x.Version,
                x.CarpetaId,
                x.FechaCreacion,
                x.FechaModificacion
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        return Ok(file);
    }

    public sealed record DownloadZipRequest(Guid[]? FileIds, Guid[]? FolderIds);

    [HttpPost("download-zip")]
    public async Task<IActionResult> DownloadZip(
        [FromBody] DownloadZipRequest request,
        CancellationToken cancellationToken)
    {
        var fileIds = (request.FileIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        var folderIds = (request.FolderIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (fileIds.Length + folderIds.Length is < 1 or > 100)
            return BadRequest(new { message = "Selecciona entre 1 y 100 elementos." });

        const int maxFiles = 1000;
        const long maxBytes = 256L * 1024 * 1024;

        var allFolders = await _dbContext.Carpetas.AsNoTracking()
            .Where(x => !x.EnPapelera)
            .Select(x => new { x.Id, x.Nombre, x.CarpetaPadreId })
            .ToListAsync(cancellationToken);
        var foldersById = allFolders.ToDictionary(x => x.Id);
        if (folderIds.Any(id => !foldersById.ContainsKey(id)))
            return NotFound(new { message = "Alguna carpeta no existe." });

        var childrenByParent = allFolders
            .Where(x => x.CarpetaPadreId.HasValue)
            .ToLookup(x => x.CarpetaPadreId!.Value);

        var selectedFolders = new Dictionary<Guid, string>();
        var queue = new Queue<(Guid Id, string Path)>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderId in folderIds)
        {
            var folder = foldersById[folderId];
            var rootName = SafeZipName(folder.Nombre, folder.Id);
            var candidate = rootName;
            var suffix = 1;
            while (!usedRoots.Add(candidate))
                candidate = $"{rootName} ({suffix++})";
            queue.Enqueue((folderId, candidate));
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (id, path) = queue.Dequeue();
            if (!selectedFolders.TryAdd(id, path))
                continue;
            if (selectedFolders.Count > 10000)
                return BadRequest(new { message = "Demasiadas carpetas para una descarga ZIP." });
            foreach (var child in childrenByParent[id])
                queue.Enqueue((child.Id, $"{path}/{SafeZipName(child.Nombre, child.Id)}"));
        }

        foreach (var id in selectedFolders.Keys)
        {
            if (!await _permissionService.HasPermissionAsync(
                    CurrentUserId(), id, PermisoCarpeta.Leer, cancellationToken))
                return Forbid();
        }

        var includedFolderIds = selectedFolders.Keys.ToArray();
        var allFiles = await _dbContext.Archivos.AsNoTracking()
            .Where(x => x.Estado == EstadoArchivo.Activo &&
                (fileIds.Contains(x.Id) || includedFolderIds.Contains(x.CarpetaId)))
            .ToListAsync(cancellationToken);
        if (fileIds.Any(id => allFiles.All(x => x.Id != id)))
            return NotFound(new { message = "Algún archivo no existe." });

        var entries = new List<(Archivo File, string Path)>();
        foreach (var file in allFiles)
        {
            if (selectedFolders.TryGetValue(file.CarpetaId, out var parentPath))
                entries.Add((file, $"{parentPath}/{SafeZipName(file.Nombre, file.Id)}"));
            else if (fileIds.Contains(file.Id))
                entries.Add((file, SafeZipName(file.Nombre, file.Id)));
        }
        if (entries.Count > maxFiles || entries.Sum(x => x.File.TamanoBytes) > maxBytes)
            return BadRequest(new { message = "Límite ZIP: 1000 archivos y 256 MB sin comprimir." });

        foreach (var (file, _) in entries)
        {
            if (!await _permissionService.HasPermissionAsync(
                    CurrentUserId(), file.CarpetaId, PermisoCarpeta.Leer, cancellationToken))
                return Forbid();
            if (!await _fileStorage.ExistsAsync(file.NombreFisico, cancellationToken))
                return NotFound(new { message = "Falta un archivo físico de la selección." });
        }

        var output = new MemoryStream();
        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in selectedFolders.Values.OrderBy(x => x.Count(c => c == '/')))
                {
                    var folderEntry = folder.TrimEnd('/') + "/";
                    if (usedPaths.Add(folderEntry))
                        zip.CreateEntry(folderEntry);
                }

                foreach (var (file, path) in entries)
                {
                    var candidate = path;
                    var suffix = 1;
                    var parent = path.LastIndexOf('/');
                    var prefix = parent >= 0 ? path[..(parent + 1)] : "";
                    var name = parent >= 0 ? path[(parent + 1)..] : path;
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    while (!usedPaths.Add(candidate))
                        candidate = $"{prefix}{stem} ({suffix++}){ext}";

                    var entry = zip.CreateEntry(candidate, CompressionLevel.Fastest);
                    await using var source = await _fileStorage.OpenReadAsync(file.NombreFisico, cancellationToken);
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination, cancellationToken);
                }
            }
            output.Position = 0;
            return File(output, "application/zip", "archivos-seleccionados.zip");
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private static string SafeZipName(string name, Guid id)
    {
        var safe = new string(name.Where(c => c != '/' && c != '\\' &&
            c >= ' ' && c != ':' && c != '\u007f').ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".."
            ? id.ToString("N")
            : safe;
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        if (!await _fileStorage.ExistsAsync(
                file.NombreFisico,
                cancellationToken))
        {
            return NotFound(new
            {
                message = "El archivo f�sico no existe."
            });
        }

        var stream = await _fileStorage.OpenReadAsync(
            file.NombreFisico,
            cancellationToken);

        return File(
            stream,
            file.TipoContenido,
            file.Nombre,
            enableRangeProcessing: true);
    }

    [HttpGet("{id:guid}/open")]
    public async Task<IActionResult> Open(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        if (!await _fileStorage.ExistsAsync(
                file.NombreFisico,
                cancellationToken))
        {
            return NotFound(new
            {
                message = "El archivo f�sico no existe."
            });
        }

        var stream = await _fileStorage.OpenReadAsync(
            file.NombreFisico,
            cancellationToken);

        Response.Headers.ContentDisposition =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(file.Nombre)}";
        Response.Headers.XContentTypeOptions = "nosniff";

        var contentType = string.Equals(
            Path.GetExtension(file.Nombre),
            ".txt",
            StringComparison.OrdinalIgnoreCase)
            ? "text/plain; charset=utf-8"
            : file.TipoContenido;

        return File(
            stream,
            contentType,
            enableRangeProcessing: true);
    }

    [HttpPut("{id:guid}/rename")]
    public async Task<IActionResult> Rename(
        Guid id,
        RenameFileRequest request,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Modificar,
                cancellationToken))
        {
            return Forbid();
        }

        if (!FileNameValidator.TryValidate(
                request.Name,
                out var newName,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        var duplicateExists = await _dbContext.Archivos
            .AnyAsync(
                x =>
                    x.Id != id &&
                    x.CarpetaId == file.CarpetaId &&
                    x.Nombre == newName &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre."
            });
        }

        if (file.Nombre == newName)
            return NoContent();

        file.Nombre = newName;
        file.Extension = Path.GetExtension(newName);
        file.Version++;
        file.FechaModificacion = DateTime.UtcNow;

        await _syncChangeService.RegisterAsync(
            file.CarpetaId,
            file.Id,
            null,
            "FILE_RENAMED",
            file.Version,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpPut("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, MoveFileRequest request, CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos.FirstOrDefaultAsync(x => x.Id == id && x.Estado == EstadoArchivo.Activo, cancellationToken);
        if (file is null) return NotFound();
        var destination = await _dbContext.Carpetas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DestinationFolderId && !x.EnPapelera, cancellationToken);
        if (destination is null) return NotFound(new { message = "La carpeta de destino no existe." });
        if (file.CarpetaId == destination.Id) return NoContent();
        var sourceRoot = await FindRootAsync(file.CarpetaId, cancellationToken);
        var destinationRoot = await FindRootAsync(destination.Id, cancellationToken);
        if (!sourceRoot.HasValue || sourceRoot != destinationRoot)
            return BadRequest(new { message = "No se permite mover elementos entre raíces distintas." });
        var userId = CurrentUserId();
        if (!await _permissionService.HasPermissionAsync(userId, file.CarpetaId, PermisoCarpeta.Modificar, cancellationToken) ||
            !await _permissionService.HasPermissionAsync(userId, destination.Id, PermisoCarpeta.Crear, cancellationToken)) return Forbid();
        if (await _dbContext.Archivos.AnyAsync(x => x.Id != id && x.CarpetaId == destination.Id && x.Nombre == file.Nombre && x.Estado == EstadoArchivo.Activo, cancellationToken) ||
            await _dbContext.Carpetas.AnyAsync(x => x.CarpetaPadreId == destination.Id && x.Nombre == file.Nombre && !x.EnPapelera, cancellationToken))
            return Conflict(new { message = "Ya existe un elemento con ese nombre en el destino." });
        var oldParentId = file.CarpetaId;
        file.CarpetaId = destination.Id;
        file.FechaModificacion = DateTime.UtcNow;
        await _syncChangeService.RegisterAsync(oldParentId, file.Id, null, "FILE_MOVED", file.Version, cancellationToken: cancellationToken);
        await _syncChangeService.RegisterAsync(destination.Id, file.Id, null, "FILE_MOVED", file.Version, cancellationToken: cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> MoveToTrash(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Eliminar,
                cancellationToken))
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;

        file.Estado = EstadoArchivo.EnPapelera;
        file.FechaEliminacion = now;
        file.FechaModificacion = now;
        file.EliminacionLoteId = Guid.NewGuid();
        file.Version++;

        await _syncChangeService.RegisterAsync(
            file.CarpetaId,
            file.Id,
            null,
            "FILE_DELETED",
            file.Version,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    private Guid CurrentUserId()
    {
        var value = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
    private async Task<Guid?> FindRootAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var current = folderId;
        while (visited.Add(current))
        {
            var folder = await _dbContext.Carpetas.AsNoTracking()
                .Where(x => x.Id == current && !x.EnPapelera)
                .Select(x => new { x.Id, x.CarpetaPadreId })
                .FirstOrDefaultAsync(cancellationToken);
            if (folder is null) return null;
            if (!folder.CarpetaPadreId.HasValue) return folder.Id;
            current = folder.CarpetaPadreId.Value;
        }
        return null;
    }

}

public sealed record RenameFileRequest(string Name);
public sealed record MoveFileRequest(Guid DestinationFolderId);
