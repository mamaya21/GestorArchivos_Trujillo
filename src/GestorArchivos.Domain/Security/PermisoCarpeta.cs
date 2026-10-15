namespace GestorArchivos.Domain.Security;

[Flags]
public enum PermisoCarpeta
{
    Ninguno = 0,
    Leer = 1,
    Crear = 2,
    Modificar = 4,
    Eliminar = 8,
    Administrar = 16,

    Escritura = Crear | Modificar,
    ControlTotal = Leer | Crear | Modificar | Eliminar | Administrar
}