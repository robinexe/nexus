using Nexus.WindowsActions;

namespace Nexus.Tests;

/// <summary>
/// Revisão de segurança dos módulos: a pasta de dados do aplicador elevado (%ProgramData%\NEXUS) era criada com a ACL herdada,
/// que deixa usuários comuns criarem e alterarem arquivos (diário, manifestos usados na restauração com privilégio).
/// A aplicação da ACL só roda no Windows; aqui fica a regra que decide se a pasta é confiável.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminOnlyDirectoryRuleTests
{
    private const string User = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    // Máscaras Win32: FILE_GENERIC_READ|EXECUTE, FILE_ALL_ACCESS, FILE_WRITE_DATA, FILE_APPEND_DATA, WRITE_DAC, FILE_DELETE_CHILD.
    private const int ReadAndExecute = 0x1200A9, FullControl = 0x1F01FF, WriteData = 0x2, AppendData = 0x4, ChangePermissions = 0x40000, DeleteChild = 0x40;

    private static AccessEntry E(string sid, int r, bool allow = true) => new(sid, r, allow);

    [Fact]
    public void Installer_acl_is_trusted()
    {
        Assert.Null(AdminOnlyDirectory.Evaluate(AdminOnlyDirectory.AdministratorsSid,
        [
            E(AdminOnlyDirectory.LocalSystemSid, FullControl), E(AdminOnlyDirectory.AdministratorsSid, FullControl),
            E(AdminOnlyDirectory.UsersSid, ReadAndExecute), E(AdminOnlyDirectory.CreatorOwnerSid, FullControl),
        ]));
    }

    [Fact]
    public void ProgramData_default_acl_is_refused()
    {
        // ACL padrão de %ProgramData%: Usuários podem criar arquivos (WriteData) e pastas (AppendData).
        Assert.NotNull(AdminOnlyDirectory.Evaluate(AdminOnlyDirectory.AdministratorsSid,
        [
            E(AdminOnlyDirectory.AdministratorsSid, FullControl), E(AdminOnlyDirectory.UsersSid, ReadAndExecute),
            E(AdminOnlyDirectory.UsersSid, WriteData | AppendData),
        ]));
    }

    [Fact]
    public void Folder_owned_or_writable_by_a_standard_user_is_refused()
    {
        Assert.NotNull(AdminOnlyDirectory.Evaluate(User, [E(AdminOnlyDirectory.AdministratorsSid, FullControl)]));
        Assert.NotNull(AdminOnlyDirectory.Evaluate(null, []));
        Assert.NotNull(AdminOnlyDirectory.Evaluate(AdminOnlyDirectory.LocalSystemSid, [E(User, ChangePermissions)]));
        Assert.NotNull(AdminOnlyDirectory.Evaluate(AdminOnlyDirectory.LocalSystemSid, [E(User, DeleteChild)]));
        // Negar escrita não é conceder: só entradas "Allow" contam.
        Assert.Null(AdminOnlyDirectory.Evaluate(AdminOnlyDirectory.LocalSystemSid, [E(User, FullControl, allow: false)]));
    }
}
