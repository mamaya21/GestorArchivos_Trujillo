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

    private Task<bool> IsSelectorManagedFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        return (
            from item in _dbContext.ElementosSync.AsNoTracking()
            join device in _dbContext.DispositivosSync.AsNoTracking()
                on item.DispositivoId equals device.Id
            join file in _dbContext.Archivos.AsNoTracking()
                on item.ArchivoRemotoId equals file.Id
            where device.Activo && item.Activo && item.Tipo == TipoElementoSync.Archivo &&
                  file.Id == fileId && file.CarpetaId == device.CarpetaRaizId
            select item.Id
        ).AnyAsync(cancellationToken);
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


    public sealed record CopySelectionRequest(
        Guid DestinationFolderId,
        Guid[]? FileIds,
        Guid[]? FolderIds,
        string? ConflictResolution = null);

    [HttpPost("copy-selection")]
    public async Task<IActionResult> CopySelection(
        CopySelectionRequest request,
        CancellationToken cancellationToken)
    {
        if (await _dbContext.DispositivosSync.AsNoTracking().AnyAsync(
                x => x.CarpetaRaizId == request.DestinationFolderId, cancellationToken))
            return Conflict(new { message = "El destino principal de sincronización se administra desde el selector." });
        var destination = await _dbContext.Carpetas.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.DestinationFolderId && !x.EnPapelera, cancellationToken);
        if (destination is null) return NotFound(new { message = "Carpeta de destino no disponible." });
        var userId = CurrentUserId();
        if (!await _permissionService.HasPermissionAsync(userId, destination.Id, PermisoCarpeta.Crear, cancellationToken))
            return Forbid();

        var folderIds = (request.FolderIds ?? []).Distinct().ToArray();
        var fileIds = (request.FileIds ?? []).Distinct().ToArray();
        if (folderIds.Length + fileIds.Length != 1)
            return BadRequest(new { message = "Copie un elemento por solicitud para resolver conflictos de forma segura." });

        var sourceFolder = folderIds.Length == 1
            ? await _dbContext.Carpetas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == folderIds[0] && !x.EnPapelera, cancellationToken)
            : null;
        var sourceFile = fileIds.Length == 1
            ? await _dbContext.Archivos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileIds[0] && x.Estado == EstadoArchivo.Activo, cancellationToken)
            : null;
        if (sourceFolder is null && sourceFile is null) return NotFound();
        if (sourceFolder is not null && !sourceFolder.CarpetaPadreId.HasValue)
            return BadRequest(new { message = "No se puede copiar una carpeta raíz protegida." });

        var originalName = sourceFolder?.Nombre ?? sourceFile!.Nombre;
        var isFolder = sourceFolder is not null;
        async Task<bool> ExistsAsync(Guid parentId, string name) =>
            await _dbContext.Carpetas.AnyAsync(x => x.CarpetaPadreId == parentId && x.Nombre == name && !x.EnPapelera, cancellationToken) ||
            await _dbContext.Archivos.AnyAsync(x => x.CarpetaId == parentId && x.Nombre == name && x.Estado == EstadoArchivo.Activo, cancellationToken);

        var finalName = originalName;
        Archivo? replaceTarget = null;
        if (await ExistsAsync(destination.Id, finalName))
        {
            if (request.ConflictResolution == "skip") return Ok(new { skipped = true });
            if (request.ConflictResolution == "replace")
            {
                if (sourceFile is null)
                    return Conflict(new { message = "El reemplazo de carpetas requiere resolver cada archivo por separado." });
                replaceTarget = await _dbContext.Archivos.FirstOrDefaultAsync(
                    x => x.CarpetaId == destination.Id && x.Nombre == originalName && x.Estado == EstadoArchivo.Activo,
                    cancellationToken);
                if (replaceTarget is null)
                    return Conflict(new { message = "El nombre pertenece a una carpeta. No puede reemplazarse con un archivo." });
                if (replaceTarget.Id == sourceFile.Id)
                    return Ok(new { skipped = true });
                if (!await _permissionService.HasPermissionAsync(userId, destination.Id, PermisoCarpeta.Modificar, cancellationToken))
                    return Forbid();
            }
            else if (request.ConflictResolution != "keepBoth")
                return Conflict(new { message = "Ya existe un elemento con ese nombre en el destino." });

            var ext = isFolder ? string.Empty : Path.GetExtension(originalName);
            var stem = ext.Length == 0 ? originalName : originalName[..^ext.Length];
            var found = false;
            for (var i = 1; i <= 10000; i++)
            {
                var candidate = $"{stem} ({i}){ext}";
                if (!FileNameValidator.TryValidate(candidate, out _, out _)) break;
                if (await ExistsAsync(destination.Id, candidate)) continue;
                finalName = candidate;
                found = true;
                break;
            }
            if (!found) return Conflict(new { message = "No se pudo generar un nombre disponible." });
        }

        // La copia completa se prepara antes de persistir. Los archivos físicos
        // nuevos se eliminan si falla cualquier paso.
        var copiedPaths = new List<string>();
        var committed = false;
        var visited = new HashSet<Guid>();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            async Task CopyFileAsync(Archivo original, Guid parentId, string name)
            {
                if (!await _permissionService.HasPermissionAsync(userId, original.CarpetaId, PermisoCarpeta.Leer, cancellationToken))
                    throw new UnauthorizedAccessException("Sin permiso de lectura sobre un archivo.");

                var newId = Guid.NewGuid();
                var physical = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"),
                    $"{newId:N}{Path.GetExtension(original.Nombre)}");
                await using (var input = await _fileStorage.OpenReadAsync(original.NombreFisico, cancellationToken))
                    await _fileStorage.SaveAsync(physical, input, cancellationToken);
                copiedPaths.Add(physical);

                _dbContext.Archivos.Add(new Archivo
                {
                    Id = newId, CarpetaId = parentId, Nombre = name,
                    NombreFisico = physical, Extension = Path.GetExtension(name),
                    TipoContenido = original.TipoContenido, TamanoBytes = original.TamanoBytes,
                    HashSha256 = original.HashSha256, Version = 1, CreadoPorUsuarioId = userId
                });
                await _syncChangeService.RegisterAsync(parentId, newId, null, "FILE_CREATED", 1,
                    cancellationToken: cancellationToken);
            }

            async Task CopyFolderAsync(Guid originalId, Guid parentId, string name)
            {
                if (!visited.Add(originalId))
                    throw new InvalidOperationException("Se detectó un ciclo en la estructura de carpetas.");
                if (!await _permissionService.HasPermissionAsync(userId, originalId, PermisoCarpeta.Leer, cancellationToken))
                    throw new UnauthorizedAccessException("Sin permiso de lectura sobre una subcarpeta.");

                var newFolder = new Carpeta { Id = Guid.NewGuid(), Nombre = name,
                    CarpetaPadreId = parentId, CreadoPorUsuarioId = userId };
                _dbContext.Carpetas.Add(newFolder);
                await _syncChangeService.RegisterAsync(parentId, null, newFolder.Id, "FOLDER_CREATED",
                    cancellationToken: cancellationToken);

                var originals = await _dbContext.Archivos.AsNoTracking()
                    .Where(x => x.CarpetaId == originalId && x.Estado == EstadoArchivo.Activo)
                    .ToListAsync(cancellationToken);
                foreach (var original in originals)
                    await CopyFileAsync(original, newFolder.Id, original.Nombre);

                var children = await _dbContext.Carpetas.AsNoTracking()
                    .Where(x => x.CarpetaPadreId == originalId && !x.EnPapelera)
                    .ToListAsync(cancellationToken);
                foreach (var child in children)
                    await CopyFolderAsync(child.Id, newFolder.Id, child.Nombre);
            }

            if (replaceTarget is not null && sourceFile is not null)
            {
                if (!await _permissionService.HasPermissionAsync(userId, sourceFile.CarpetaId, PermisoCarpeta.Leer, cancellationToken))
                    return Forbid();

                var nextId = Guid.NewGuid();
                var newPath = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"),
                    $"{nextId:N}{Path.GetExtension(sourceFile.Nombre)}");
                await using (var input = await _fileStorage.OpenReadAsync(sourceFile.NombreFisico, cancellationToken))
                    await _fileStorage.SaveAsync(newPath, input, cancellationToken);
                copiedPaths.Add(newPath);

                _dbContext.ArchivoVersiones.Add(new ArchivoVersion
                {
                    ArchivoId = replaceTarget.Id, Numero = replaceTarget.Version,
                    NombreFisico = replaceTarget.NombreFisico, HashSha256 = replaceTarget.HashSha256,
                    TamanoBytes = replaceTarget.TamanoBytes, TipoContenido = replaceTarget.TipoContenido,
                    UsuarioId = userId
                });
                replaceTarget.NombreFisico = newPath;
                replaceTarget.HashSha256 = sourceFile.HashSha256;
                replaceTarget.TamanoBytes = sourceFile.TamanoBytes;
                replaceTarget.TipoContenido = sourceFile.TipoContenido;
                replaceTarget.Version++;
                replaceTarget.FechaModificacion = DateTime.UtcNow;
                await _syncChangeService.RegisterAsync(destination.Id, replaceTarget.Id, null, "FILE_UPDATED",
                    replaceTarget.Version, cancellationToken: cancellationToken);
            }
            else if (sourceFile is not null) await CopyFileAsync(sourceFile, destination.Id, finalName);
            else await CopyFolderAsync(sourceFolder!.Id, destination.Id, finalName);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return Ok(new { copied = true, name = finalName });
        }
        catch (UnauthorizedAccessException) { await transaction.RollbackAsync(CancellationToken.None); return Forbid(); }
        catch (Exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (!committed)
            {
                _dbContext.ChangeTracker.Clear();
                foreach (var path in copiedPaths)
                {
                    try { await _fileStorage.DeleteAsync(path, CancellationToken.None); }
                    catch { /* Se preserva el error original; limpieza pendiente en almacenamiento. */ }
                }
            }
        }
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> ListVersions(Guid id, CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == id && x.Estado == EstadoArchivo.Activo, cancellationToken);
        if (file is null) return NotFound();
        if (!await _permissionService.HasPermissionAsync(CurrentUserId(), file.CarpetaId, PermisoCarpeta.Leer, cancellationToken))
            return Forbid();
        var history = await _dbContext.ArchivoVersiones.AsNoTracking()
            .Where(x => x.ArchivoId == id).OrderByDescending(x => x.Numero)
            .Select(x => new { x.Id, x.Numero, x.TamanoBytes, x.FechaUtc })
            .ToListAsync(cancellationToken);
        return Ok(new { current = file.Version, history });
    }

    [HttpGet("{id:guid}/versions/{versionId:guid}/download")]
    public async Task<IActionResult> DownloadVersion(Guid id, Guid versionId, CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == id && x.Estado == EstadoArchivo.Activo, cancellationToken);
        if (file is null) return NotFound();
        if (!await _permissionService.HasPermissionAsync(CurrentUserId(), file.CarpetaId, PermisoCarpeta.Leer, cancellationToken))
            return Forbid();
        var version = await _dbContext.ArchivoVersiones.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == versionId && x.ArchivoId == id, cancellationToken);
        if (version is null) return NotFound();
        var stream = await _fileStorage.OpenReadAsync(version.NombreFisico, cancellationToken);
        return File(stream, version.TipoContenido, file.Nombre);
    }

    [HttpPost("{id:guid}/versions/{versionId:guid}/restore")]
    public async Task<IActionResult> RestoreVersion(Guid id, Guid versionId, CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos.FirstOrDefaultAsync(
            x => x.Id == id && x.Estado == EstadoArchivo.Activo, cancellationToken);
        if (file is null) return NotFound();
        if (!await _permissionService.HasPermissionAsync(CurrentUserId(), file.CarpetaId, PermisoCarpeta.Modificar, cancellationToken))
            return Forbid();
        var previous = await _dbContext.ArchivoVersiones.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == versionId && x.ArchivoId == id, cancellationToken);
        if (previous is null) return NotFound();
        var newPath = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"),
            $"{Guid.NewGuid():N}{Path.GetExtension(file.Nombre)}");
        await using (var input = await _fileStorage.OpenReadAsync(previous.NombreFisico, cancellationToken))
            await _fileStorage.SaveAsync(newPath, input, cancellationToken);
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            _dbContext.ArchivoVersiones.Add(new ArchivoVersion
            {
                ArchivoId = file.Id, Numero = file.Version, NombreFisico = file.NombreFisico,
                HashSha256 = file.HashSha256, TamanoBytes = file.TamanoBytes,
                TipoContenido = file.TipoContenido, UsuarioId = CurrentUserId()
            });
            file.NombreFisico = newPath;
            file.HashSha256 = previous.HashSha256;
            file.TamanoBytes = previous.TamanoBytes;
            file.TipoContenido = previous.TipoContenido;
            file.Version++;
            file.FechaModificacion = DateTime.UtcNow;
            await _syncChangeService.RegisterAsync(file.CarpetaId, file.Id, null, "FILE_UPDATED",
                file.Version, cancellationToken: cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Ok(new { file.Id, file.Version });
        }
        catch
        {
            await _fileStorage.DeleteAsync(newPath, CancellationToken.None);
            throw;
        }
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

        // Ignore selected descendants: they are already included in their ancestor folder.
        var selectedRootIds = new HashSet<Guid>(folderIds);
        foreach (var folderId in folderIds)
        {
            var visited = new HashSet<Guid> { folderId };
            var parentId = foldersById[folderId].CarpetaPadreId;
            while (parentId.HasValue && visited.Add(parentId.Value))
            {
                if (selectedRootIds.Contains(parentId.Value))
                {
                    selectedRootIds.Remove(folderId);
                    break;
                }
                if (!foldersById.TryGetValue(parentId.Value, out var parent))
                    break;
                parentId = parent.CarpetaPadreId;
            }
        }

        var selectedFolders = new Dictionary<Guid, string>();
        var queue = new Queue<(Guid Id, string Path)>();
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderId in selectedRootIds)
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

        if (file is null) return NotFound();
        if (await IsSelectorManagedFileAsync(id, cancellationToken))
            return Conflict(new { message = "Este archivo está seleccionado para sincronización. Quítalo primero del selector de este dispositivo." });

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
        if (await IsSelectorManagedFileAsync(id, cancellationToken))
            return Conflict(new { message = "Este archivo está seleccionado para sincronización. Quítalo primero del selector de este dispositivo." });
        if (await _dbContext.DispositivosSync.AsNoTracking().AnyAsync(
                x => x.CarpetaRaizId == request.DestinationFolderId, cancellationToken))
            return Conflict(new { message = "El destino principal de sincronización se administra desde el selector." });
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
        // Conservar ambos genera un nombre disponible sin alterar el elemento existente.
        // Omitir deja el origen intacto y permite continuar una selección múltiple.
        async Task<bool> ExistsAsync(string name) =>
            await _dbContext.Carpetas.AnyAsync(x => x.Id != id && x.CarpetaPadreId == destination.Id && x.Nombre == name && !x.EnPapelera, cancellationToken) ||
            await _dbContext.Archivos.AnyAsync(x => x.Id != id && x.CarpetaId == destination.Id && x.Nombre == name && x.Estado == EstadoArchivo.Activo, cancellationToken);

        var finalName = file.Nombre;
        if (await ExistsAsync(finalName) && request.ConflictResolution == "replace")
        {
            var target = await _dbContext.Archivos.FirstOrDefaultAsync(
                x => x.Id != file.Id && x.CarpetaId == destination.Id &&
                     x.Nombre == file.Nombre && x.Estado == EstadoArchivo.Activo, cancellationToken);
            if (target is null) return Conflict(new { message = "No es posible reemplazar una carpeta con un archivo." });
            if (!await _permissionService.HasPermissionAsync(userId, destination.Id, PermisoCarpeta.Modificar, cancellationToken))
                return Forbid();

            var newPath = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"),
                $"{Guid.NewGuid():N}{Path.GetExtension(file.Nombre)}");
            await using (var input = await _fileStorage.OpenReadAsync(file.NombreFisico, cancellationToken))
                await _fileStorage.SaveAsync(newPath, input, cancellationToken);
            try
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                _dbContext.ArchivoVersiones.Add(new ArchivoVersion
                {
                    ArchivoId = target.Id, Numero = target.Version,
                    NombreFisico = target.NombreFisico, HashSha256 = target.HashSha256,
                    TamanoBytes = target.TamanoBytes, TipoContenido = target.TipoContenido,
                    UsuarioId = userId
                });
                target.NombreFisico = newPath;
                target.HashSha256 = file.HashSha256;
                target.TamanoBytes = file.TamanoBytes;
                target.TipoContenido = file.TipoContenido;
                target.Version++;
                target.FechaModificacion = DateTime.UtcNow;
                var sourceParentId = file.CarpetaId;
                file.Estado = EstadoArchivo.EnPapelera;
                file.FechaEliminacion = DateTime.UtcNow;
                file.FechaModificacion = DateTime.UtcNow;
                await _syncChangeService.RegisterAsync(sourceParentId, file.Id, null, "FILE_DELETED",
                    file.Version, cancellationToken: cancellationToken);
                await _syncChangeService.RegisterAsync(destination.Id, target.Id, null, "FILE_UPDATED",
                    target.Version, cancellationToken: cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Ok(new { replaced = true });
            }
            catch
            {
                await _fileStorage.DeleteAsync(newPath, CancellationToken.None);
                throw;
            }
        }
        if (await ExistsAsync(finalName))
        {
            if (request.ConflictResolution == "skip")
                return Ok(new { skipped = true });

            if (request.ConflictResolution != "keepBoth")
                return Conflict(new { message = "Ya existe un elemento con ese nombre en el destino." });

            var extension = Path.GetExtension(finalName);
            var stem = extension.Length == 0 ? finalName : finalName[..^extension.Length];
            var found = false;
            for (var suffix = 1; suffix <= 10000; suffix++)
            {
                var candidate = $"{stem} ({suffix}){extension}";
                if (!FileNameValidator.TryValidate(candidate, out _, out _))
                    return Conflict(new { message = "No se pudo generar un nombre válido." });
                if (await ExistsAsync(candidate)) continue;
                finalName = candidate;
                found = true;
                break;
            }
            if (!found)
                return Conflict(new { message = "No hay un nombre disponible en el destino." });
        }
        file.Nombre = finalName;
        file.Extension = Path.GetExtension(finalName);
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

        if (file is null) return NotFound();
        if (await IsSelectorManagedFileAsync(id, cancellationToken))
            return Conflict(new { message = "Este archivo está seleccionado para sincronización. Quítalo primero del selector de este dispositivo." });

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
public sealed record MoveFileRequest(Guid DestinationFolderId, string? ConflictResolution = null);
