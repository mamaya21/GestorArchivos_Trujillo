using System.Security.Claims;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Identity.Seed;
using GestorArchivos.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Api.Controllers.Trash;

[ApiController]
[Authorize]
[Route("api/trash")]
public sealed class TrashController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IFolderPermissionService _permissionService;
    private readonly string _rootPath;

    public TrashController(
        ApplicationDbContext dbContext,
        IFolderPermissionService permissionService,
        IOptions<StorageOptions> storageOptions)
    {
        _dbContext = dbContext;
        _permissionService = permissionService;

        if (string.IsNullOrWhiteSpace(
                storageOptions.Value.RootPath))
        {
            throw new InvalidOperationException(
                "Storage:RootPath no está configurado.");
        }

        _rootPath = Path.GetFullPath(
            storageOptions.Value.RootPath);
    }

    private Task<bool> IsSelectorManagedTrashRootAsync(
        Carpeta folder, CancellationToken cancellationToken)
    {
        return _dbContext.ElementosSync.AsNoTracking().AnyAsync(
            x => x.Activo && x.Tipo == TipoElementoSync.Carpeta &&
                 x.CarpetaRemotaId == folder.Id, cancellationToken);
    }

    [HttpGet]
    public async Task<IActionResult> GetContents(
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var isAdmin = IsAdministrator();

        var folderCandidates =
            await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x => x.EnPapelera)
                .OrderByDescending(
                    x => x.FechaEliminacion)
                .Select(x => new
                {
                    x.Id,
                    x.Nombre,
                    x.CarpetaPadreId,
                    x.FechaEliminacion,
                    x.EliminacionLoteId
                })
                .ToListAsync(cancellationToken);

        // Mostrar únicamente raíces eliminadas. Los descendientes
        // siguen dentro de su carpeta original y se restauran con ella.
        var trashedFolderIds = folderCandidates
            .Select(x => x.Id)
            .ToHashSet();
        var managedRootIds = await _dbContext.ElementosSync.AsNoTracking()
            .Where(x => x.Activo && x.Tipo == TipoElementoSync.Carpeta &&
                        x.CarpetaRemotaId.HasValue)
            .Select(x => x.CarpetaRemotaId!.Value)
            .ToListAsync(cancellationToken);
        var managedRoots = managedRootIds.ToHashSet();

        var visibleFolders = new List<object>();

        foreach (var folder in folderCandidates)
        {
            if (folder.CarpetaPadreId.HasValue &&
                trashedFolderIds.Contains(folder.CarpetaPadreId.Value))
                continue;

            if (isAdmin ||
                await _permissionService
                    .HasPermissionIncludingTrashAsync(
                        userId,
                        folder.Id,
                        PermisoCarpeta.Leer,
                        cancellationToken))
            {
                visibleFolders.Add(new
                {
                    type = "folder",
                    folder.Id,
                    folder.Nombre,
                    folder.CarpetaPadreId,
                    folder.FechaEliminacion,
                    selectorManaged = managedRoots.Contains(folder.Id)
                });
            }
        }

        var fileCandidates =
            await _dbContext.Archivos
                .AsNoTracking()
                .Where(x =>
                    x.Estado ==
                    EstadoArchivo.EnPapelera)
                .OrderByDescending(
                    x => x.FechaEliminacion)
                .Select(x => new
                {
                    x.Id,
                    x.Nombre,
                    x.CarpetaId,
                    x.TamanoBytes,
                    x.FechaEliminacion,
                    x.EliminacionLoteId
                })
                .ToListAsync(cancellationToken);

        var visibleFiles = new List<object>();

        foreach (var file in fileCandidates)
        {
            // Los archivos de carpetas eliminadas pertenecen al árbol
            // de esa carpeta; no son entradas independientes de papelera.
            if (trashedFolderIds.Contains(file.CarpetaId))
                continue;

            if (isAdmin ||
                await _permissionService
                    .HasPermissionIncludingTrashAsync(
                        userId,
                        file.CarpetaId,
                        PermisoCarpeta.Leer,
                        cancellationToken))
            {
                visibleFiles.Add(new
                {
                    type = "file",
                    file.Id,
                    file.Nombre,
                    file.CarpetaId,
                    file.TamanoBytes,
                    file.FechaEliminacion
                });
            }
        }

        return Ok(new
        {
            folders = visibleFolders,
            files = visibleFiles
        });
    }

    [HttpPost("folders/{id:guid}/restore")]
    public async Task<IActionResult> RestoreFolder(
        Guid id,
        [FromQuery] string? conflictResolution,
        CancellationToken cancellationToken)
    {
        var rootFolder =
            await _dbContext.Carpetas
                .FirstOrDefaultAsync(
                    x =>
                        x.Id == id &&
                        x.EnPapelera,
                    cancellationToken);

        if (rootFolder is null)
            return NotFound();

        if (await IsSelectorManagedTrashRootAsync(rootFolder, cancellationToken))
            return Conflict(new { message = "La restauración de esta raíz sincronizada debe gestionarse desde el selector de este dispositivo." });

        if (!await _permissionService
                .HasPermissionIncludingTrashAsync(
                    CurrentUserId(),
                    id,
                    PermisoCarpeta.Modificar,
                    cancellationToken))
        {
            return Forbid();
        }

        if (rootFolder.CarpetaPadreId.HasValue)
        {
            var parentId =
                rootFolder.CarpetaPadreId.Value;

            var parentActive =
                await _dbContext.Carpetas
                    .AsNoTracking()
                    .AnyAsync(
                        x =>
                            x.Id == parentId &&
                            !x.EnPapelera,
                        cancellationToken);

            if (!parentActive)
            {
                return Conflict(new
                {
                    message =
                        "La carpeta padre no está disponible. Restaure primero la carpeta padre."
                });
            }

            if (!await _permissionService
                    .HasPermissionAsync(
                        CurrentUserId(),
                        parentId,
                        PermisoCarpeta.Crear,
                        cancellationToken))
            {
                return Forbid();
            }
        }
        else if (!IsAdministrator())
        {
            return Forbid();
        }

        var duplicateRoot =
            await _dbContext.Carpetas
                .AsNoTracking()
                .AnyAsync(
                    x =>
                        x.Id != rootFolder.Id &&
                        x.CarpetaPadreId ==
                        rootFolder.CarpetaPadreId &&
                        x.Nombre == rootFolder.Nombre &&
                        !x.EnPapelera,
                    cancellationToken);

        if (duplicateRoot && conflictResolution == "skip") return Ok(new { skipped = true });
        if (duplicateRoot && conflictResolution == "keepBoth")
        {
            var baseName = rootFolder.Nombre;
            var found = false;
            for (var n = 1; n <= 10000; n++)
            {
                var candidate = $"{baseName} ({n})";
                if (!FileNameValidator.TryValidate(candidate, out _, out _)) break;
                if (await _dbContext.Carpetas.AnyAsync(x => x.Id != rootFolder.Id && x.CarpetaPadreId == rootFolder.CarpetaPadreId && x.Nombre == candidate && !x.EnPapelera, cancellationToken)) continue;
                rootFolder.Nombre = candidate;
                found = true;
                break;
            }
            if (!found) return Conflict(new { message = "No se pudo generar un nombre disponible." });
            duplicateRoot = false;
        }
        if (duplicateRoot)
        {
            return Conflict(new
            {
                message =
                    "Ya existe una carpeta activa con el mismo nombre en el destino."
            });
        }

        var batchId = rootFolder.EliminacionLoteId;

        List<GestorArchivos.Domain.Entities.Carpeta> folders;
        List<GestorArchivos.Domain.Entities.Archivo> files;

        if (batchId.HasValue)
        {
            folders = await _dbContext.Carpetas
                .Where(x =>
                    x.EnPapelera &&
                    x.EliminacionLoteId ==
                    batchId.Value)
                .ToListAsync(cancellationToken);

            files = await _dbContext.Archivos
                .Where(x =>
                    x.Estado ==
                    EstadoArchivo.EnPapelera &&
                    x.EliminacionLoteId ==
                    batchId.Value)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Compatibilidad con elementos enviados a papelera
            // antes de incorporar EliminacionLoteId.
            folders =
            [
                rootFolder
            ];

            files = [];
        }

        var restoringFolderIds =
            folders
                .Select(x => x.Id)
                .ToHashSet();

        foreach (var folder in folders)
        {
            if (folder.Id == rootFolder.Id)
                continue;

            if (!folder.CarpetaPadreId.HasValue ||
                !restoringFolderIds.Contains(
                    folder.CarpetaPadreId.Value))
            {
                return Conflict(new
                {
                    message =
                        $"La carpeta '{folder.Nombre}' no pertenece a una estructura restaurable válida."
                });
            }
        }

        foreach (var folder in folders)
        {
            var duplicate =
                await _dbContext.Carpetas
                    .AsNoTracking()
                    .AnyAsync(
                        x =>
                            !x.EnPapelera &&
                            x.Id != folder.Id &&
                            x.CarpetaPadreId ==
                            folder.CarpetaPadreId &&
                            x.Nombre == folder.Nombre,
                        cancellationToken);

            if (duplicate && conflictResolution == "keepBoth")
            {
                var originalName = folder.Nombre;
                var renamed = false;
                for (var n = 1; n <= 10000; n++)
                {
                    var candidate = $"{originalName} ({n})";
                    if (!FileNameValidator.TryValidate(candidate, out _, out _)) break;
                    if (await _dbContext.Carpetas.AnyAsync(x => x.Id != folder.Id && x.CarpetaPadreId == folder.CarpetaPadreId && x.Nombre == candidate && !x.EnPapelera, cancellationToken)) continue;
                    folder.Nombre = candidate;
                    renamed = true;
                    break;
                }
                if (!renamed) return Conflict(new { message = "No se pudo generar un nombre disponible." });
            }
            else if (duplicate)
            {
                return Conflict(new
                {
                    message =
                        $"No se puede restaurar la carpeta '{folder.Nombre}' porque existe otra carpeta activa con el mismo nombre."
                });
            }
        }

        foreach (var file in files)
        {
            if (!restoringFolderIds.Contains(
                    file.CarpetaId))
            {
                return Conflict(new
                {
                    message =
                        $"El archivo '{file.Nombre}' no pertenece a una carpeta válida del lote."
                });
            }

            var duplicate =
                await _dbContext.Archivos
                    .AsNoTracking()
                    .AnyAsync(
                        x =>
                            x.Id != file.Id &&
                            x.CarpetaId ==
                            file.CarpetaId &&
                            x.Nombre == file.Nombre &&
                            x.Estado ==
                            EstadoArchivo.Activo,
                        cancellationToken);

            if (duplicate)
            {
                return Conflict(new
                {
                    message =
                        $"No se puede restaurar el archivo '{file.Nombre}' porque existe otro archivo activo con el mismo nombre."
                });
            }
        }

        var now = DateTime.UtcNow;

        foreach (var folder in folders)
        {
            folder.EnPapelera = false;
            folder.FechaEliminacion = null;
            folder.FechaModificacion = now;
            folder.EliminacionLoteId = null;
        }

        foreach (var file in files)
        {
            file.Estado = EstadoArchivo.Activo;
            file.FechaEliminacion = null;
            file.FechaModificacion = now;
            file.EliminacionLoteId = null;
        }

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpPost("files/{id:guid}/restore")]
    public async Task<IActionResult> RestoreFile(
        Guid id,
        [FromQuery] string? conflictResolution,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado ==
                    EstadoArchivo.EnPapelera,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService
                .HasPermissionIncludingTrashAsync(
                    CurrentUserId(),
                    file.CarpetaId,
                    PermisoCarpeta.Crear,
                    cancellationToken))
        {
            return Forbid();
        }

        var folderActive =
            await _dbContext.Carpetas
                .AsNoTracking()
                .AnyAsync(
                    x =>
                        x.Id == file.CarpetaId &&
                        !x.EnPapelera,
                    cancellationToken);

        if (!folderActive)
        {
            return Conflict(new
            {
                message =
                    "La carpeta contenedora está en papelera o ya no existe."
            });
        }

        var duplicate =
            await _dbContext.Archivos
                .AsNoTracking()
                .AnyAsync(
                    x =>
                        x.Id != id &&
                        x.CarpetaId == file.CarpetaId &&
                        x.Nombre == file.Nombre &&
                        x.Estado ==
                        EstadoArchivo.Activo,
                    cancellationToken);

        if (duplicate && conflictResolution == "replace")
        {
            if (!await _permissionService.HasPermissionIncludingTrashAsync(
                CurrentUserId(), file.CarpetaId, PermisoCarpeta.Modificar, cancellationToken))
                return Forbid();
            var target = await _dbContext.Archivos.FirstAsync(
                x => x.Id != id && x.CarpetaId == file.CarpetaId &&
                     x.Nombre == file.Nombre && x.Estado == EstadoArchivo.Activo, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            _dbContext.ArchivoVersiones.Add(new ArchivoVersion
            {
                ArchivoId = target.Id, Numero = target.Version, NombreFisico = target.NombreFisico,
                HashSha256 = target.HashSha256, TamanoBytes = target.TamanoBytes,
                TipoContenido = target.TipoContenido, UsuarioId = CurrentUserId()
            });
            target.NombreFisico = file.NombreFisico;
            target.HashSha256 = file.HashSha256;
            target.TamanoBytes = file.TamanoBytes;
            target.TipoContenido = file.TipoContenido;
            target.Version++;
            target.FechaModificacion = DateTime.UtcNow;
            _dbContext.Archivos.Remove(file);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Ok(new { replaced = true });
        }
        if (duplicate && conflictResolution == "skip") return Ok(new { skipped = true });
        if (duplicate && conflictResolution == "keepBoth")
        {
            var extension = Path.GetExtension(file.Nombre);
            var stem = extension.Length == 0 ? file.Nombre : file.Nombre[..^extension.Length];
            var found = false;
            for (var n = 1; n <= 10000; n++)
            {
                var candidate = $"{stem} ({n}){extension}";
                if (!FileNameValidator.TryValidate(candidate, out _, out _)) break;
                if (await _dbContext.Archivos.AnyAsync(x => x.Id != id && x.CarpetaId == file.CarpetaId && x.Nombre == candidate && x.Estado == EstadoArchivo.Activo, cancellationToken)) continue;
                file.Nombre = candidate;
                file.Extension = extension;
                found = true;
                break;
            }
            if (!found) return Conflict(new { message = "No se pudo generar un nombre disponible." });
            duplicate = false;
        }
        if (duplicate)
        {
            return Conflict(new
            {
                message =
                    "Ya existe un archivo activo con ese nombre."
            });
        }

        file.Estado = EstadoArchivo.Activo;
        file.FechaEliminacion = null;
        file.FechaModificacion = DateTime.UtcNow;
        file.EliminacionLoteId = null;

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }
    [HttpDelete("files/{id:guid}")]
    public async Task<IActionResult> DeleteFilePermanently(
    Guid id,
    CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.EnPapelera,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService
                .HasPermissionIncludingTrashAsync(
                    CurrentUserId(),
                    file.CarpetaId,
                    PermisoCarpeta.Eliminar,
                    cancellationToken))
        {
            return Forbid();
        }

        var physicalPath =
            GetPhysicalPath(file.NombreFisico);

        var operationId = Guid.NewGuid();

        var quarantineDirectory =
            GetQuarantineDirectory(operationId);

        string? quarantinePath = null;

        try
        {
            if (System.IO.File.Exists(physicalPath))
            {
                Directory.CreateDirectory(
                    quarantineDirectory);

                quarantinePath = Path.Combine(
                    quarantineDirectory,
                    Guid.NewGuid().ToString("N"));

                System.IO.File.Move(
                    physicalPath,
                    quarantinePath);
            }
        }
        catch (IOException)
        {
            CleanupEmptyDirectory(
                quarantineDirectory);

            return Conflict(new
            {
                message =
                    "No se pudo preparar el archivo físico para su eliminación."
            });
        }
        catch (UnauthorizedAccessException)
        {
            CleanupEmptyDirectory(
                quarantineDirectory);

            return Conflict(new
            {
                message =
                    "No se pudo preparar el archivo físico para su eliminación."
            });
        }

        try
        {
            _dbContext.Archivos.Remove(file);

            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
        catch
        {
            if (quarantinePath is not null &&
                System.IO.File.Exists(quarantinePath))
            {
                RestoreQuarantinedFile(
                    quarantinePath,
                    physicalPath);
            }

            CleanupEmptyDirectory(
                quarantineDirectory);

            throw;
        }

        if (quarantinePath is not null)
        {
            TryDeleteQuarantinedFile(
                quarantinePath);
        }

        CleanupEmptyDirectory(
            quarantineDirectory);

        return NoContent();
    }

    [HttpDelete("folders/{id:guid}")]
    public async Task<IActionResult> DeleteFolderPermanently(
        Guid id,
        CancellationToken cancellationToken)
    {
        var rootFolder =
            await _dbContext.Carpetas
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x =>
                        x.Id == id &&
                        x.EnPapelera,
                    cancellationToken);

        if (rootFolder is null)
            return NotFound();

        var folderIds =
            await GetSubtreeFolderIdsAsync(
                id,
                cancellationToken);

        foreach (var folderId in folderIds)
        {
            if (!await _permissionService
                    .HasPermissionIncludingTrashAsync(
                        CurrentUserId(),
                        folderId,
                        PermisoCarpeta.Eliminar,
                        cancellationToken))
            {
                return Forbid();
            }
        }

        var activeFolderExists =
            await _dbContext.Carpetas
                .AnyAsync(
                    x =>
                        folderIds.Contains(x.Id) &&
                        !x.EnPapelera,
                    cancellationToken);

        var activeFileExists =
            await _dbContext.Archivos
                .AnyAsync(
                    x =>
                        folderIds.Contains(x.CarpetaId) &&
                        x.Estado == EstadoArchivo.Activo,
                    cancellationToken);

        if (activeFolderExists ||
            activeFileExists)
        {
            return Conflict(new
            {
                message =
                    "La estructura contiene elementos activos y no puede eliminarse definitivamente."
            });
        }

        var files = await _dbContext.Archivos
            .Where(x =>
                folderIds.Contains(x.CarpetaId))
            .ToListAsync(cancellationToken);

        var folders = await _dbContext.Carpetas
            .Where(x =>
                folderIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var operationId = Guid.NewGuid();

        var quarantineDirectory =
            GetQuarantineDirectory(operationId);

        var movedFiles =
            new List<QuarantinedFile>();

        try
        {
            foreach (var file in files)
            {
                var originalPath =
                    GetPhysicalPath(
                        file.NombreFisico);

                if (!System.IO.File.Exists(
                        originalPath))
                {
                    continue;
                }

                Directory.CreateDirectory(
                    quarantineDirectory);

                var quarantinePath =
                    Path.Combine(
                        quarantineDirectory,
                        Guid.NewGuid()
                            .ToString("N"));

                System.IO.File.Move(
                    originalPath,
                    quarantinePath);

                movedFiles.Add(
                    new QuarantinedFile(
                        originalPath,
                        quarantinePath));
            }
        }
        catch (IOException)
        {
            RestoreQuarantinedFiles(
                movedFiles);

            CleanupEmptyDirectory(
                quarantineDirectory);

            return Conflict(new
            {
                message =
                    "No se pudieron preparar todos los archivos físicos para su eliminación."
            });
        }
        catch (UnauthorizedAccessException)
        {
            RestoreQuarantinedFiles(
                movedFiles);

            CleanupEmptyDirectory(
                quarantineDirectory);

            return Conflict(new
            {
                message =
                    "No se pudieron preparar todos los archivos físicos para su eliminación."
            });
        }

        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    cancellationToken);

        try
        {
            _dbContext.Archivos.RemoveRange(
                files);

            await _dbContext.SaveChangesAsync(
                cancellationToken);

            var remainingFolderIds =
                new HashSet<Guid>(
                    folderIds);

            while (remainingFolderIds.Count > 0)
            {
                var parentIds =
                    folders
                        .Where(x =>
                            remainingFolderIds
                                .Contains(x.Id) &&
                            x.CarpetaPadreId
                                .HasValue &&
                            remainingFolderIds
                                .Contains(
                                    x.CarpetaPadreId
                                        .Value))
                        .Select(x =>
                            x.CarpetaPadreId!
                                .Value)
                        .ToHashSet();

                var leaves =
                    folders
                        .Where(x =>
                            remainingFolderIds
                                .Contains(x.Id) &&
                            !parentIds.Contains(
                                x.Id))
                        .ToList();

                if (leaves.Count == 0)
                {
                    throw new
                        InvalidOperationException(
                            "No fue posible determinar el orden de eliminación de carpetas.");
                }

                _dbContext.Carpetas
                    .RemoveRange(leaves);

                await _dbContext
                    .SaveChangesAsync(
                        cancellationToken);

                foreach (var folder in leaves)
                {
                    remainingFolderIds
                        .Remove(folder.Id);
                }
            }

            await transaction.CommitAsync(
                cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);

            RestoreQuarantinedFiles(
                movedFiles);

            CleanupEmptyDirectory(
                quarantineDirectory);

            throw;
        }

        foreach (var movedFile in movedFiles)
        {
            TryDeleteQuarantinedFile(
                movedFile.QuarantinePath);
        }

        CleanupEmptyDirectory(
            quarantineDirectory);

        return NoContent();
    }

    private async Task<HashSet<Guid>>
        GetSubtreeFolderIdsAsync(
            Guid rootId,
            CancellationToken cancellationToken)
    {
        var folderIds =
            new HashSet<Guid>
            {
                rootId
            };

        var currentLevel =
            new List<Guid>
            {
                rootId
            };

        while (currentLevel.Count > 0)
        {
            var children =
                await _dbContext.Carpetas
                    .AsNoTracking()
                    .Where(x =>
                        x.CarpetaPadreId.HasValue &&
                        currentLevel.Contains(
                            x.CarpetaPadreId.Value))
                    .Select(x => x.Id)
                    .ToListAsync(
                        cancellationToken);

            currentLevel =
                children
                    .Where(folderIds.Add)
                    .ToList();
        }

        return folderIds;
    }

    private string GetQuarantineDirectory(
        Guid operationId)
    {
        var quarantineRoot =
            Path.Combine(
                _rootPath,
                ".delete-pending");

        var fullPath =
            Path.GetFullPath(
                Path.Combine(
                    quarantineRoot,
                    operationId.ToString("N")));

        var rootWithSeparator =
            _rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ruta de cuarentena inválida.");
        }

        return fullPath;
    }

    private static void RestoreQuarantinedFiles(
        IEnumerable<QuarantinedFile> files)
    {
        foreach (var file in files.Reverse())
        {
            RestoreQuarantinedFile(
                file.QuarantinePath,
                file.OriginalPath);
        }
    }

    private static void RestoreQuarantinedFile(
        string quarantinePath,
        string originalPath)
    {
        if (!System.IO.File.Exists(
                quarantinePath))
        {
            return;
        }

        var directory =
            Path.GetDirectoryName(
                originalPath);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        if (System.IO.File.Exists(
                originalPath))
        {
            throw new IOException(
                $"No se puede restaurar '{originalPath}' porque ya existe un archivo en la ubicación original.");
        }

        System.IO.File.Move(
            quarantinePath,
            originalPath);
    }

    private static void TryDeleteQuarantinedFile(
        string quarantinePath)
    {
        try
        {
            if (System.IO.File.Exists(
                    quarantinePath))
            {
                System.IO.File.Delete(
                    quarantinePath);
            }
        }
        catch (IOException)
        {
            // SQL ya confirmó la eliminación.
            // El archivo permanece aislado en .delete-pending
            // para limpieza posterior.
        }
        catch (UnauthorizedAccessException)
        {
            // SQL ya confirmó la eliminación.
            // El archivo permanece aislado en .delete-pending
            // para limpieza posterior.
        }
    }

    private static void CleanupEmptyDirectory(
        string directory)
    {
        try
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(
                    directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool IsAdministrator()
    {
        return User.IsInRole(
            IdentitySeeder.AdministratorRole);
    }

    private Guid CurrentUserId()
    {
        var value =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(
                value,
                out var userId))
        {
            throw new
                UnauthorizedAccessException();
        }

        return userId;
    }

    private string GetPhysicalPath(
        string relativePath)
    {
        if (Path.IsPathRooted(
                relativePath))
        {
            throw new InvalidOperationException(
                "No se permiten rutas físicas absolutas.");
        }

        var fullPath =
            Path.GetFullPath(
                Path.Combine(
                    _rootPath,
                    relativePath));

        var rootWithSeparator =
            _rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ruta física inválida.");
        }

        return fullPath;
    }

    private sealed record QuarantinedFile(
        string OriginalPath,
        string QuarantinePath);
}