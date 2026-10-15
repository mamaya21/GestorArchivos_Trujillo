using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using GestorArchivos.Application.Common.Interfaces;

namespace GestorArchivos.Api.Controllers.Uploads;

[ApiController]
[Authorize]
[Route("api/uploads")]
public class UploadsController : ControllerBase
{
    private const long MaxFileSize = 4L * 1024 * 1024 * 1024;
    private const int ChunkSize = 64 * 1024 * 1024;
    private const int UploadExpirationHours = 24;

    private readonly UploadLockManager _lockManager;

    private readonly ApplicationDbContext _dbContext;
    private readonly IFolderPermissionService _permissionService;
    private readonly string _rootPath;
    private readonly ISyncChangeService _syncChangeService;

    public UploadsController(
    ApplicationDbContext dbContext,
    IOptions<StorageOptions> storageOptions,
    IFolderPermissionService permissionService,
    UploadLockManager lockManager,
    ISyncChangeService syncChangeService)
    {
        _dbContext = dbContext;
        _permissionService = permissionService;
        _lockManager = lockManager;
        _syncChangeService = syncChangeService;

        if (string.IsNullOrWhiteSpace(storageOptions.Value.RootPath))
            throw new InvalidOperationException(
                "Storage:RootPath no está configurado.");

        _rootPath = Path.GetFullPath(
            storageOptions.Value.RootPath);
    }

    [HttpPost("initialize")]
    public async Task<IActionResult> Initialize(
        InitializeUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileNameValidator.TryValidate(
                request.FileName,
                out var fileName,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        if (request.FileSize <= 0 ||
            request.FileSize > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "Tamaño de archivo inválido."
            });
        }

        var folderExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == request.FolderId &&
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
                GetCurrentUserId(),
                request.FolderId,
                PermisoCarpeta.Crear,
                cancellationToken))
        {
            return Forbid();
        }

        Archivo? replacement = null;
        if (request.ReplaceFileId.HasValue)
        {
            if (!request.ExpectedVersion.HasValue || request.ExpectedVersion.Value < 1)
                return BadRequest(new { message = "Indica la versión esperada." });

            replacement = await _dbContext.Archivos.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == request.ReplaceFileId.Value &&
                    x.CarpetaId == request.FolderId && x.Estado == EstadoArchivo.Activo,
                    cancellationToken);
            if (replacement is null) return NotFound();
            if (replacement.Version != request.ExpectedVersion.Value)
                return Conflict(new { message = "El archivo cambió. Actualiza la carpeta antes de reemplazarlo." });
            if (!string.Equals(replacement.Nombre.Normalize(System.Text.NormalizationForm.FormC), fileName.Normalize(System.Text.NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase))
                return Conflict(new { message = "El nombre no coincide con el archivo existente." });
            if (!await _permissionService.HasPermissionAsync(GetCurrentUserId(), request.FolderId,
                    PermisoCarpeta.Modificar, cancellationToken)) return Forbid();
        }

        var duplicateExists = await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x => x.CarpetaId == request.FolderId &&
                     x.Nombre == fileName &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicateExists && replacement is null)
        {
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre."
            });
        }

        var uploadId = Guid.NewGuid();
        var uploadDirectory = GetUploadDirectory(uploadId);

        Directory.CreateDirectory(uploadDirectory);

        var expectedChunkCount = checked(
            (int)((request.FileSize + ChunkSize - 1) / ChunkSize));

        var metadata = new UploadMetadata(
            request.FolderId,
            fileName,
            request.FileSize,
            string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType,
            GetCurrentUserId(),
            ChunkSize,
            expectedChunkCount,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(UploadExpirationHours),
            request.ReplaceFileId,
            request.ExpectedVersion);

        await System.IO.File.WriteAllTextAsync(
            Path.Combine(uploadDirectory, "metadata.json"),
            JsonSerializer.Serialize(metadata),
            cancellationToken);

        return Ok(new
        {
            uploadId,
            chunkSize = ChunkSize,
            expectedChunkCount,
            expiresAtUtc = metadata.ExpiresAtUtc
        });
    }

    [HttpPut("{uploadId:guid}/chunks/{chunkIndex:int}")]
    [RequestSizeLimit(ChunkSize)]
    public async Task<IActionResult> UploadChunk(
        Guid uploadId,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        var semaphore = GetLock(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var metadata = await ReadMetadataAsync(
                uploadId,
                cancellationToken);

            if (metadata is null)
                return NotFound();

            var ownershipResult = ValidateOwnership(metadata);

            if (ownershipResult is not null)
                return ownershipResult;

            if (!await _permissionService.HasPermissionAsync(
                GetCurrentUserId(),
                metadata.FolderId,
                PermisoCarpeta.Crear,
                cancellationToken))
            {
                return Forbid();
            }

            if (IsExpired(metadata))
            {
                DeleteUploadDirectory(uploadId);

                return StatusCode(
                    StatusCodes.Status410Gone,
                    new
                    {
                        message = "La sesión de carga ha expirado."
                    });
            }

            if (chunkIndex < 0 ||
                chunkIndex >= metadata.ExpectedChunkCount)
            {
                return BadRequest(new
                {
                    message = "Índice de fragmento inválido."
                });
            }

            var expectedLength = GetExpectedChunkLength(
                metadata,
                chunkIndex);

            if (Request.ContentLength.HasValue &&
                Request.ContentLength.Value != expectedLength)
            {
                return BadRequest(new
                {
                    message = "Tamaño de fragmento inválido.",
                    expectedBytes = expectedLength,
                    receivedBytes = Request.ContentLength.Value
                });
            }

            var uploadDirectory = GetUploadDirectory(uploadId);
            var chunkPath = GetChunkPath(
                uploadDirectory,
                chunkIndex);

            var tempPath =
                chunkPath + "." +
                Guid.NewGuid().ToString("N") +
                ".tmp";

            if (System.IO.File.Exists(chunkPath))
            {
                var existingLength =
                    new FileInfo(chunkPath).Length;

                if (existingLength == expectedLength)
                    return NoContent();

                return Conflict(new
                {
                    message = "Existe un fragmento inválido con ese índice."
                });
            }

            try
            {
                long bytesWritten = 0;

                await using (var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];

                    while (true)
                    {
                        var read = await Request.Body.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken);

                        if (read == 0)
                            break;

                        bytesWritten += read;

                        if (bytesWritten > expectedLength)
                        {
                            return BadRequest(new
                            {
                                message = "El fragmento supera el tamaño esperado."
                            });
                        }

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                    }
                }

                if (bytesWritten != expectedLength)
                {
                    return BadRequest(new
                    {
                        message = "El fragmento está incompleto.",
                        expectedBytes = expectedLength,
                        receivedBytes = bytesWritten
                    });
                }

                System.IO.File.Move(
                    tempPath,
                    chunkPath);

                return NoContent();
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    [HttpGet("{uploadId:guid}/status")]
    public async Task<IActionResult> Status(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var semaphore = GetLock(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var metadata = await ReadMetadataAsync(
                uploadId,
                cancellationToken);

            if (metadata is null)
                return NotFound();

            var ownershipResult = ValidateOwnership(metadata);

            if (ownershipResult is not null)
                return ownershipResult;

            if (IsExpired(metadata))
            {
                DeleteUploadDirectory(uploadId);

                return StatusCode(
                    StatusCodes.Status410Gone,
                    new
                    {
                        message = "La sesión de carga ha expirado."
                    });
            }

            var uploadDirectory =
                GetUploadDirectory(uploadId);

            var chunks = Directory
                .EnumerateFiles(uploadDirectory, "*.chunk")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(x =>
                    int.TryParse(x, out var index)
                        ? index
                        : -1)
                .Where(x =>
                    x >= 0 &&
                    x < metadata.ExpectedChunkCount)
                .OrderBy(x => x)
                .ToArray();

            return Ok(new
            {
                uploadId,
                metadata.FileSize,
                metadata.ChunkSize,
                metadata.ExpectedChunkCount,
                metadata.ExpiresAtUtc,
                receivedChunks = chunks
            });
        }
        finally
        {
            semaphore.Release();
        }
    }

    [HttpPost("{uploadId:guid}/complete")]
    public async Task<IActionResult> Complete(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var semaphore = GetLock(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        string? finalPath = null;

        try
        {
            var metadata = await ReadMetadataAsync(
                uploadId,
                cancellationToken);

            if (metadata is null)
                return NotFound();

            var ownershipResult = ValidateOwnership(metadata);

            if (ownershipResult is not null)
                return ownershipResult;

            if (!await _permissionService.HasPermissionAsync(
                    GetCurrentUserId(),
                    metadata.FolderId,
                    metadata.ReplaceFileId.HasValue ? PermisoCarpeta.Modificar : PermisoCarpeta.Crear,
                    cancellationToken))
            {
                return Forbid();
            }

            if (IsExpired(metadata))
            {
                DeleteUploadDirectory(uploadId);

                return StatusCode(
                    StatusCodes.Status410Gone,
                    new
                    {
                        message = "La sesión de carga ha expirado."
                    });
            }

            var folderExists = await _dbContext.Carpetas
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id == metadata.FolderId &&
                         !x.EnPapelera,
                    cancellationToken);

            if (!folderExists)
            {
                return Conflict(new
                {
                    message = "La carpeta de destino ya no está disponible."
                });
            }

            var uploadDirectory =
                GetUploadDirectory(uploadId);

            for (var index = 0;
                 index < metadata.ExpectedChunkCount;
                 index++)
            {
                var chunkPath = GetChunkPath(
                    uploadDirectory,
                    index);

                if (!System.IO.File.Exists(chunkPath))
                {
                    return BadRequest(new
                    {
                        message = "La carga está incompleta.",
                        missingChunk = index
                    });
                }

                var actualLength =
                    new FileInfo(chunkPath).Length;

                var expectedLength =
                    GetExpectedChunkLength(
                        metadata,
                        index);

                if (actualLength != expectedLength)
                {
                    return BadRequest(new
                    {
                        message = "Se encontró un fragmento con tamaño inválido.",
                        chunkIndex = index,
                        expectedBytes = expectedLength,
                        receivedBytes = actualLength
                    });
                }
            }

            var unexpectedChunks = Directory
                .EnumerateFiles(uploadDirectory, "*.chunk")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(x =>
                    int.TryParse(x, out var index)
                        ? index
                        : -1)
                .Any(index =>
                    index < 0 ||
                    index >= metadata.ExpectedChunkCount);

            if (unexpectedChunks)
            {
                return BadRequest(new
                {
                    message = "La carga contiene fragmentos inesperados."
                });
            }

            var fileId = Guid.NewGuid();
            var extension =
                Path.GetExtension(metadata.FileName);

            var relativePath = Path.Combine(
                DateTime.UtcNow.ToString("yyyy"),
                DateTime.UtcNow.ToString("MM"),
                $"{fileId:N}{extension}");

            finalPath = GetSafeStoragePath(relativePath);

            Directory.CreateDirectory(
                Path.GetDirectoryName(finalPath)!);

            using var sha256 =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);

            long assembledBytes = 0;

            await using (var output = new FileStream(
                finalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];

                for (var index = 0;
                     index < metadata.ExpectedChunkCount;
                     index++)
                {
                    var chunkPath = GetChunkPath(
                        uploadDirectory,
                        index);

                    await using var input =
                        new FileStream(
                            chunkPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            1024 * 1024,
                            FileOptions.Asynchronous |
                            FileOptions.SequentialScan);

                    while (true)
                    {
                        var read = await input.ReadAsync(
                            buffer.AsMemory(
                                0,
                                buffer.Length),
                            cancellationToken);

                        if (read == 0)
                            break;

                        assembledBytes += read;

                        sha256.AppendData(
                            buffer,
                            0,
                            read);

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                    }
                }
            }

            if (assembledBytes != metadata.FileSize)
            {
                throw new InvalidDataException(
                    "El tamaño ensamblado no coincide con el esperado.");
            }

            var hash = Convert.ToHexString(
                sha256.GetHashAndReset());

            if (metadata.ReplaceFileId.HasValue)
            {
                var target = await _dbContext.Archivos.FirstOrDefaultAsync(x =>
                    x.Id == metadata.ReplaceFileId.Value &&
                    x.CarpetaId == metadata.FolderId &&
                    x.Estado == EstadoArchivo.Activo, cancellationToken);

                if (target is null || !metadata.ExpectedVersion.HasValue ||
                    target.Version != metadata.ExpectedVersion.Value ||
                    !string.Equals(target.Nombre.Normalize(System.Text.NormalizationForm.FormC), metadata.FileName.Normalize(System.Text.NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Delete(finalPath);
                    finalPath = null;
                    return Conflict(new { message = "El archivo fue modificado o eliminado. No se reemplazó." });
                }

                var previousPath = target.NombreFisico;
                target.NombreFisico = relativePath;
                target.TamanoBytes = metadata.FileSize;
                target.TipoContenido = metadata.ContentType;
                target.HashSha256 = hash;
                target.Version++;
                target.FechaModificacion = DateTime.UtcNow;

                _dbContext.Set<ArchivoFisicoPendienteEliminacion>().Add(
                    new ArchivoFisicoPendienteEliminacion
                    {
                        Id = Guid.NewGuid(),
                        RutaFisica = previousPath,
                        FechaRegistroUtc = DateTime.UtcNow,
                        EliminarDespuesUtc = DateTime.UtcNow.AddDays(7)
                    });

                await _syncChangeService.RegisterAsync(metadata.FolderId, target.Id, null,
                    "FILE_UPDATED", target.Version, cancellationToken: cancellationToken);

                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _dbContext.ChangeTracker.Clear();
                    System.IO.File.Delete(finalPath);
                    finalPath = null;
                    return Conflict(new { message = "Otra actualización ganó la carrera. No se reemplazó." });
                }

                DeleteUploadDirectory(uploadId);
                finalPath = null;
                return Ok(new
                {
                    target.Id,
                    target.Nombre,
                    target.TamanoBytes,
                    target.HashSha256,
                    target.Version
                });
            }

            var duplicateExists =
                await _dbContext.Archivos
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.CarpetaId == metadata.FolderId &&
                             x.Nombre == metadata.FileName &&
                             x.Estado == EstadoArchivo.Activo,
                        cancellationToken);

            if (duplicateExists)
            {
                System.IO.File.Delete(finalPath);
                finalPath = null;

                return Conflict(new
                {
                    message = "Ya existe un archivo con ese nombre."
                });
            }

            var entity = new Archivo
            {
                Id = fileId,
                Nombre = metadata.FileName,
                NombreFisico = relativePath,
                Extension = extension,
                TipoContenido = metadata.ContentType,
                TamanoBytes = metadata.FileSize,
                HashSha256 = hash,
                Version = 1,
                CarpetaId = metadata.FolderId,
                CreadoPorUsuarioId = metadata.UserId
            };

            _dbContext.Archivos.Add(entity);

            await _syncChangeService.RegisterAsync(
                metadata.FolderId,
                entity.Id,
                null,
                "FILE_CREATED",
                entity.Version,
                cancellationToken: cancellationToken);

            try
            {
                await _dbContext.SaveChangesAsync(
                    cancellationToken);
            }
            catch
            {
                if (System.IO.File.Exists(finalPath))
                    System.IO.File.Delete(finalPath);

                finalPath = null;
                throw;
            }

            DeleteUploadDirectory(uploadId);
            finalPath = null;

            return Ok(new
            {
                entity.Id,
                entity.Nombre,
                entity.TamanoBytes,
                entity.HashSha256,
                entity.Version
            });
        }
        catch
        {
            if (finalPath is not null &&
                System.IO.File.Exists(finalPath))
            {
                System.IO.File.Delete(finalPath);
            }

            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    [HttpDelete("{uploadId:guid}")]
    public async Task<IActionResult> Cancel(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var semaphore = GetLock(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var metadata = await ReadMetadataAsync(
                uploadId,
                cancellationToken);

            if (metadata is null)
                return NotFound();

            var ownershipResult = ValidateOwnership(metadata);

            if (ownershipResult is not null)
                return ownershipResult;

            DeleteUploadDirectory(uploadId);

            return NoContent();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private Guid GetCurrentUserId()
    {
        var value = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }

    private IActionResult? ValidateOwnership(
        UploadMetadata metadata)
    {
        return metadata.UserId == GetCurrentUserId()
            ? null
            : NotFound();
    }

    private static bool IsExpired(
        UploadMetadata metadata)
    {
        return metadata.ExpiresAtUtc <= DateTime.UtcNow;
    }

    private static long GetExpectedChunkLength(
        UploadMetadata metadata,
        int chunkIndex)
    {
        if (chunkIndex <
            metadata.ExpectedChunkCount - 1)
        {
            return metadata.ChunkSize;
        }

        return metadata.FileSize -
               ((long)chunkIndex *
                metadata.ChunkSize);
    }

    private async Task<UploadMetadata?> ReadMetadataAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(
            GetUploadDirectory(uploadId),
            "metadata.json");

        if (!System.IO.File.Exists(metadataPath))
            return null;

        var json =
            await System.IO.File.ReadAllTextAsync(
                metadataPath,
                cancellationToken);

        return JsonSerializer.Deserialize<UploadMetadata>(
            json);
    }

    private SemaphoreSlim GetLock(Guid uploadId)
    {
        return _lockManager.Get(uploadId);
    }

    private string GetUploadDirectory(Guid uploadId)
    {
        return GetSafeStoragePath(
            Path.Combine(
                ".uploads",
                uploadId.ToString("N")));
    }

    private static string GetChunkPath(
        string uploadDirectory,
        int chunkIndex)
    {
        return Path.Combine(
            uploadDirectory,
            $"{chunkIndex:D8}.chunk");
    }

    private void DeleteUploadDirectory(Guid uploadId)
    {
        var directory =
            GetUploadDirectory(uploadId);

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private string GetSafeStoragePath(
        string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "No se permiten rutas absolutas.");
        }

        var fullPath = Path.GetFullPath(
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
                "Ruta de almacenamiento inválida.");
        }

        return fullPath;
    }

    private sealed record UploadMetadata(
        Guid FolderId,
        string FileName,
        long FileSize,
        string ContentType,
        Guid UserId,
        int ChunkSize,
        int ExpectedChunkCount,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        Guid? ReplaceFileId = null,
        long? ExpectedVersion = null);
}

public sealed record InitializeUploadRequest(
    Guid FolderId,
    string FileName,
    long FileSize,
    string? ContentType,
    Guid? ReplaceFileId = null,
    long? ExpectedVersion = null);
